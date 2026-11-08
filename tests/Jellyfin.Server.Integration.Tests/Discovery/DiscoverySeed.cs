using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// Seeds a test host once: completes the startup wizard, then adds one virtual discovery film ("Seed Film", with a
/// trailer extra) under the discovery folder and one real film in a temporary movie library. Reused by later
/// discovery tests: call <see cref="EnsureAsync"/> before anything else and authenticate with
/// <see cref="Seeded.AccessToken"/> (the wizard can only be completed once per host).
/// </summary>
public static class DiscoverySeed
{
    /// <summary>The name of the virtual discovery film.</summary>
    public const string SeedFilmName = "Seed Film";

    /// <summary>The TMDB id of the virtual discovery film.</summary>
    public const string SeedFilmTmdbId = "900001";

    /// <summary>The name of the real library film.</summary>
    public const string RealFilmName = "Real Library Film";

    private const string LibraryName = "discovery-seed-movies";

    private static readonly ConditionalWeakTable<JellyfinApplicationFactory, Task<Seeded>> _seeds = new();

    /// <summary>
    /// Seeds the host behind <paramref name="factory"/> once and returns what was created.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <returns>The seeded items.</returns>
    public static Task<Seeded> EnsureAsync(JellyfinApplicationFactory factory)
        => _seeds.GetValue(factory, SeedAsync);

    private static async Task<Seeded> SeedAsync(JellyfinApplicationFactory factory)
    {
        var client = factory.CreateClient();

        // Finishing the wizard queues a library scan. Let it finish first so it does not run alongside the seed's scan.
        var scan = factory.Services.GetRequiredService<ITaskManager>().ScheduledTasks
            .Single(t => t.ScheduledTask is RefreshMediaLibraryTask);
        var scannedBefore = scan.LastExecutionResult?.EndTimeUtc;
        var accessToken = await AuthHelper.CompleteStartupAsync(client).ConfigureAwait(false);
        await WaitForScanAsync(scan, scannedBefore).ConfigureAwait(false);

        var services = factory.Services;
        var libraryManager = services.GetRequiredService<ILibraryManager>();
        var locator = services.GetRequiredService<IDiscoveryFolderLocator>();

        // A real movie library on a temp folder with one dummy film file, resolved by the scanner itself so that a
        // later scan finds the same item again.
        var seedRoot = Path.Combine(Path.GetTempPath(), "jellyfin-discovery-seed-" + Guid.NewGuid().ToString("N")[..8]);
        var mediaPath = Path.Combine(seedRoot, "movies");
        Directory.CreateDirectory(mediaPath);
        var realFilmPath = Path.Combine(mediaPath, RealFilmName + ".mkv");
        await File.WriteAllBytesAsync(realFilmPath, new byte[1024]).ConfigureAwait(false);
        var options = new LibraryOptions
        {
            PathInfos = [new MediaPathInfo(mediaPath)],
            EnableRealtimeMonitor = false,
        };
        await libraryManager.AddVirtualFolder(LibraryName, CollectionTypeOptions.movies, options, false).ConfigureAwait(false);

        // ValidateMediaLibrary only queues the scan task; run the scan itself so it is done on return.
        await ((LibraryManager)libraryManager).ValidateMediaLibraryInternal(new Progress<double>(), default).ConfigureAwait(false);

        var library = libraryManager.GetVirtualFolders()
            .Select(f => libraryManager.GetItemById(Guid.Parse(f.ItemId)))
            .OfType<CollectionFolder>()
            .Single(f => string.Equals(f.Name, LibraryName, StringComparison.Ordinal));

        var realFilm = libraryManager.FindByPath(realFilmPath, false) as Movie
            ?? throw new InvalidOperationException("The library scan did not pick up the real seed film.");

        var folder = locator.GetFolder();
        var seedFilm = new Movie
        {
            Name = SeedFilmName,
            IsVirtualItem = true,
        };
        seedFilm.SetProviderId(MetadataProvider.Tmdb, SeedFilmTmdbId);
        seedFilm.Id = libraryManager.GetNewItemId("discovery-seed-" + SeedFilmTmdbId, typeof(Movie));
        folder.AddChild(seedFilm);

        // An extra of the virtual film: owned by it, no parent, like a pushed trailer file.
        var seedTrailer = new Trailer
        {
            Name = SeedFilmName + " Trailer",
            Path = Path.Combine(seedRoot, "seed-trailer.mkv"),
            OwnerId = seedFilm.Id,
            ExtraType = ExtraType.Trailer,
        };
        seedTrailer.Id = libraryManager.GetNewItemId(seedTrailer.Path, typeof(Trailer));
        libraryManager.CreateItem(seedTrailer, null);

        return new Seeded(accessToken, locator.FolderId, seedFilm, seedTrailer, realFilm, library);
    }

    private static async Task WaitForScanAsync(IScheduledTaskWorker scan, DateTime? scannedBefore)
    {
        // Done once the wizard's scan has completed and no queued scan follows it for a moment.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var idleSince = DateTime.MaxValue;
        while (DateTime.UtcNow < deadline)
        {
            if (scan.State != TaskState.Idle || scan.LastExecutionResult?.EndTimeUtc == scannedBefore)
            {
                idleSince = DateTime.MaxValue;
            }
            else if (idleSince == DateTime.MaxValue)
            {
                idleSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("The library scan queued by the startup wizard did not finish.");
    }

    /// <summary>
    /// What <see cref="EnsureAsync"/> created.
    /// </summary>
    /// <param name="AccessToken">An admin access token (the wizard's user).</param>
    /// <param name="FolderId">The discovery folder id.</param>
    /// <param name="SeedFilm">The virtual discovery film.</param>
    /// <param name="SeedTrailer">A trailer extra owned by the virtual film.</param>
    /// <param name="RealFilm">The real library film.</param>
    /// <param name="Library">The temporary movie library.</param>
    public sealed record Seeded(string AccessToken, Guid FolderId, Movie SeedFilm, Trailer SeedTrailer, Movie RealFilm, CollectionFolder Library);
}
