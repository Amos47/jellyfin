using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: makes pushed trailer files local trailers of discovery films, the way <c>LibraryManager.FindExtras</c> and
/// <c>BaseItem.RefreshExtras</c> make <c>-trailer</c> files extras of a library film: a <see cref="Video"/> with
/// <see cref="ExtraType.Trailer"/>, owned by the film and with no parent. Files are only ever read, never deleted.
/// </summary>
public sealed class DiscoveryTrailers : IDiscoveryTrailers, IDisposable
{
    // Far more than any real chain of links; stops a symlink loop.
    private const int MaxLinkDepth = 32;

    private readonly ILibraryManager _libraryManager;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly IDiscoveryFolderLocator _folderLocator;
    private readonly IConfigurationManager _configurationManager;
    private readonly IDiscoveryMetadataRefresher _refresher;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<DiscoveryTrailers> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryTrailers"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="filmSync">The discovery film sync.</param>
    /// <param name="folderLocator">The discovery folder locator.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="refresher">The metadata refresher, which probes the trailer file.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryTrailers(
        ILibraryManager libraryManager,
        IDiscoveryFilmSync filmSync,
        IDiscoveryFolderLocator folderLocator,
        IConfigurationManager configurationManager,
        IDiscoveryMetadataRefresher refresher,
        ILocalizationManager localization,
        ILogger<DiscoveryTrailers> logger)
    {
        _libraryManager = libraryManager;
        _filmSync = filmSync;
        _folderLocator = folderLocator;
        _configurationManager = configurationManager;
        _refresher = refresher;
        _localization = localization;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <inheritdoc />
    public async Task<DiscoveryTrailerResult> SetAsync(int tmdbId, string path, CancellationToken cancellationToken)
    {
        var film = _filmSync.FindFilm(tmdbId);
        if (film is null)
        {
            return DiscoveryTrailerResult.FilmNotFound;
        }

        var root = _configurationManager.GetConfiguration<DiscoveryOptions>(DiscoveryConfigurationFactory.Key).TrailerRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return DiscoveryTrailerResult.NoTrailerRoot;
        }

        var filePath = ResolveInsideRoot(root, path);
        if (filePath is null)
        {
            _logger.LogWarning("Refused discovery trailer {Path} for TMDB id {TmdbId}: not a file inside {Root}", path, tmdbId, root);
            return DiscoveryTrailerResult.PathRefused;
        }

        var id = _libraryManager.GetNewItemId(filePath, typeof(Video));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A list sync may have deleted the film since the first look.
            film = _filmSync.FindFilm(tmdbId);
            if (film is null)
            {
                return DiscoveryTrailerResult.FilmNotFound;
            }

            var existing = _libraryManager.GetItemById(id);
            if (existing is not null && !IsDiscoveryTrailer(existing))
            {
                _logger.LogWarning("Refused discovery trailer {Path} for TMDB id {TmdbId}: it is already the item {Name} ({Id})", filePath, tmdbId, existing.Name, existing.Id);
                return DiscoveryTrailerResult.PathInUse;
            }

            // One pushed trailer per film: any other one goes (its file stays).
            foreach (var old in GetTrailers(film.Id).Where(t => !t.Id.Equals(id)))
            {
                DeleteTrailer(old);
            }

            var trailer = existing ?? new Video { Id = id, Path = filePath };
            trailer.Name = _localization.GetServerLocalizedString("NameExtraTrailer");
            trailer.ExtraType = ExtraType.Trailer;
            trailer.OwnerId = film.Id;
            trailer.ParentId = Guid.Empty;
            CopyOwnerMetadata(film, trailer);

            try
            {
                if (existing is null)
                {
                    _logger.LogInformation("Attaching discovery trailer {Path} to {Name} ({Id})", filePath, film.Name, film.Id);
                    _libraryManager.CreateItem(trailer, null);
                }
                else
                {
                    await trailer.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DbUpdateException ex) when (_filmSync.FindFilm(tmdbId) is null)
            {
                // The sync deleted the film after the look above: the trailer's owner key no longer holds.
                _logger.LogWarning(ex, "Discovery film for TMDB id {TmdbId} was removed while attaching {Path}", tmdbId, filePath);
                return DiscoveryTrailerResult.FilmNotFound;
            }
        }
        finally
        {
            _lock.Release();
        }

        _refresher.QueueProbe(id);
        return DiscoveryTrailerResult.Set;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var film = _filmSync.FindFilm(tmdbId);
        if (film is null)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var trailer in GetTrailers(film.Id))
            {
                DeleteTrailer(trailer);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the real path of a file if it is an existing file inside the root: both are made full and every symlink
    /// in them (the file and each parent folder) is resolved before the file is checked to be inside the root.
    /// </summary>
    /// <param name="root">The trailer root.</param>
    /// <param name="path">The path to check.</param>
    /// <returns>The real path of the file, or null if it is refused.</returns>
    internal static string? ResolveInsideRoot(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root))
        {
            return null;
        }

        var realRoot = ResolveLinks(Path.GetFullPath(root), 0);
        var realFile = ResolveLinks(Path.GetFullPath(path), 0);
        if (realRoot is null || realFile is null || !Directory.Exists(realRoot) || !File.Exists(realFile))
        {
            return null;
        }

        // With the separator, so /media/discovery2 is not inside /media/discovery.
        var prefix = Path.TrimEndingDirectorySeparator(realRoot) + Path.DirectorySeparatorChar;
        return realFile.StartsWith(prefix, StringComparison.Ordinal) ? realFile : null;
    }

    private static string? ResolveLinks(string fullPath, int depth)
    {
        if (depth > MaxLinkDepth)
        {
            return null;
        }

        var current = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(current))
        {
            return null;
        }

        var segments = fullPath[current.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is { } target)
            {
                // A relative target is relative to the folder holding the link. The target may hold links itself.
                var targetPath = Path.GetFullPath(target, current);
                return ResolveLinks(Path.Combine([targetPath, .. segments[(i + 1)..]]), depth + 1);
            }

            current = next;
        }

        return current;
    }

    private static void CopyOwnerMetadata(BaseItem owner, BaseItem trailer)
    {
        // What RefreshMetadataForOwnedItem copies from the owner, so parental ratings and dates apply to the trailer.
        trailer.Genres = owner.Genres;
        trailer.Studios = owner.Studios;
        trailer.ProductionLocations = owner.ProductionLocations;
        trailer.CommunityRating = owner.CommunityRating;
        trailer.CriticRating = owner.CriticRating;
        trailer.Overview = owner.Overview;
        trailer.OfficialRating = owner.OfficialRating;
        trailer.CustomRating = owner.CustomRating;
        trailer.ProductionYear = owner.ProductionYear;
        trailer.PremiereDate = owner.PremiereDate;
    }

    private bool IsDiscoveryTrailer(BaseItem item)
        => item is Video
            && item.ExtraType == ExtraType.Trailer
            && item.ParentId.IsEmpty()
            && _libraryManager.GetItemById(item.OwnerId) is { } owner
            && owner.ParentId.Equals(_folderLocator.FolderId);

    private BaseItem[] GetTrailers(Guid filmId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeDiscovery = true,
            OwnerIds = [filmId],
            ExtraTypes = [ExtraType.Trailer],
        }).ToArray();

    private void DeleteTrailer(BaseItem trailer)
    {
        _logger.LogInformation("Removing discovery trailer {Path} ({Id}); the file stays", trailer.Path, trailer.Id);
        _libraryManager.DeleteItem(trailer, new DeleteOptions { DeleteFileLocation = false }, false);
    }
}
