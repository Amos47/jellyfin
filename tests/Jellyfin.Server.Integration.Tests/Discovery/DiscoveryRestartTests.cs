using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// Fork contract: the discovery folder, its films and their trailers survive a restart of the server. A class of its
/// own because booting a second host replaces the static services items use, which would break a shared fixture host.
/// </summary>
public sealed class DiscoveryRestartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyfin-test-data", "discovery-restart-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best effort, a locked file must not fail the test.
        }
    }

    [Fact]
    public async Task Survives_Restart()
    {
        // A real second boot of the server on the same data directory (the factory reuses the paths it is given).
        DiscoveryContractTests.Contract contract;
        using (var first = new SameRootApplicationFactory(_root))
        {
            contract = await DiscoveryContractTests.SetUpContractAsync(first);
        }

        SqliteConnection.ClearAllPools();

        using var second = new SameRootApplicationFactory(_root);
        using var client = second.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(contract.AccessToken);
        var services = second.Services;
        var locator = services.GetRequiredService<IDiscoveryFolderLocator>();
        var libraryManager = services.GetRequiredService<ILibraryManager>();

        Assert.True(locator.FolderId.Equals(contract.FolderId));
        Assert.True(locator.GetFolder().ParentId.Equals(libraryManager.RootFolder.Id));
        Assert.Contains(libraryManager.RootFolder.VirtualChildren, c => c.Id.Equals(contract.FolderId));
        var film = Assert.IsType<Movie>(libraryManager.GetItemById(contract.Film.Id));
        Assert.Equal(DiscoverySeed.SeedFilmName, film.Name);
        Assert.True(film.IsVirtualItem);
        Assert.Contains(film.GetExtras([ExtraType.Trailer]), t => t.Id.Equals(contract.TrailerId));
        var inFolder = libraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, ParentId = contract.FolderId });
        Assert.Contains(inFolder, i => i.Id.Equals(contract.Film.Id));
        Assert.True(services.GetRequiredService<IDiscoveryFilmSync>().FindFilm(DiscoveryContractTests.FilmTmdbId)?.Id.Equals(contract.Film.Id) == true);

        // And the guard holds on the new boot.
        using var response = await client.GetAsync($"Items?userId={contract.UserId:N}&recursive=true&includeItemTypes=Movie,Trailer", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Contains(items!.Items, i => i.Id.Equals(contract.RealFilm.Id));
        Assert.DoesNotContain(items.Items, i => i.Id.Equals(contract.Film.Id) || i.Id.Equals(contract.TrailerId));
    }

    private sealed class SameRootApplicationFactory(string webHostPathRoot) : JellyfinApplicationFactory(webHostPathRoot);
}
