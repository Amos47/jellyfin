using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

public sealed class DiscoveryGuardTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public DiscoveryGuardTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Items_Recursive_ExcludesDiscovery()
    {
        var (client, userId) = await SetUpAsync();

        var result = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?recursive=true&includeItemTypes=Movie&userId={userId:N}");

        Assert.Contains(result.Items, i => i.Name == DiscoverySeed.RealFilmName);
        Assert.DoesNotContain(result.Items, i => i.Name == DiscoverySeed.SeedFilmName);
    }

    [Fact]
    public async Task Items_ParentIdDiscoveryFolder_IsEmpty()
    {
        var (client, userId) = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);

        // Today the folder answers 200 with no items, plain and recursive.
        var plain = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?parentId={seed.FolderId:N}&userId={userId:N}");
        var recursive = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?parentId={seed.FolderId:N}&recursive=true&userId={userId:N}");

        Assert.Empty(plain.Items);
        Assert.Empty(recursive.Items);
    }

    [Fact]
    public async Task Items_ByIds_ReturnsDiscovery()
    {
        // Ruling: an explicit ids query bypasses the guard, so clients can refetch a played or queued film.
        var (client, userId) = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);

        var result = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?ids={seed.SeedFilm.Id:N},{seed.RealFilm.Id:N}&userId={userId:N}");

        Assert.Contains(result.Items, i => i.Id.Equals(seed.RealFilm.Id));
        Assert.Contains(result.Items, i => i.Id.Equals(seed.SeedFilm.Id));
    }

    [Fact]
    public async Task Repository_ByIds_ReturnsDiscoveryFilmAndTrailer()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        var items = libraryManager.GetItemList(new InternalItemsQuery { ItemIds = [seed.SeedFilm.Id, seed.SeedTrailer.Id] });

        Assert.Contains(items, i => i.Id.Equals(seed.SeedFilm.Id));
        Assert.Contains(items, i => i.Id.Equals(seed.SeedTrailer.Id));
    }

    [Fact]
    public async Task Scan_KeepsDiscoveryFilmAndTrailer()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        // ValidateMediaLibrary only queues the task; this runs a scan to completion.
        await ((LibraryManager)libraryManager).ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

        Assert.NotNull(libraryManager.GetItemById(seed.SeedFilm.Id));
        Assert.NotNull(libraryManager.GetItemById(seed.SeedTrailer.Id));
        var films = libraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, AncestorIds = [seed.FolderId] });
        var extras = libraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, OwnerIds = [seed.SeedFilm.Id] });
        Assert.Contains(films, i => i.Id.Equals(seed.SeedFilm.Id));
        Assert.Contains(extras, i => i.Id.Equals(seed.SeedTrailer.Id));
    }

    [Fact]
    public async Task Search_ExcludesDiscovery()
    {
        var (client, userId) = await SetUpAsync();

        var result = await GetAsync<SearchHintResult>(client, $"Search/Hints?searchTerm=Seed&userId={userId:N}");
        var control = await GetAsync<SearchHintResult>(client, $"Search/Hints?searchTerm=Real&userId={userId:N}");

        Assert.DoesNotContain(result.SearchHints, h => h.Name.StartsWith(DiscoverySeed.SeedFilmName, StringComparison.Ordinal));
        Assert.Contains(control.SearchHints, h => h.Name == DiscoverySeed.RealFilmName);
    }

    [Fact]
    public async Task Counts_ExcludeDiscovery()
    {
        var (client, userId) = await SetUpAsync();

        var counts = await GetAsync<ItemCounts>(client, $"Items/Counts?userId={userId:N}");

        Assert.Equal(1, counts.MovieCount);
        Assert.Equal(0, counts.TrailerCount);
    }

    [Fact]
    public async Task Repository_IncludeDiscovery_ReturnsIt()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        var items = libraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, AncestorIds = [seed.FolderId] });

        Assert.Contains(items, i => i.Id.Equals(seed.SeedFilm.Id));
    }

    [Fact]
    public async Task Repository_Default_ExcludesDiscovery()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        var items = libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = [seed.FolderId] });

        Assert.DoesNotContain(items, i => i.Id.Equals(seed.SeedFilm.Id));
        Assert.NotNull(libraryManager.GetItemById(seed.SeedFilm.Id));
    }

    [Fact]
    public async Task Repository_ExtraOfDiscoveryFilm_ExcludedUnlessIncludedOrByOwner()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        // Asking for the extras of an owner the caller already holds is like asking by id: the film page's local
        // trailers (BaseItem.GetExtras) need it.
        var byOwner = libraryManager.GetItemList(new InternalItemsQuery { OwnerIds = [seed.SeedFilm.Id] });
        var included = libraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, OwnerIds = [seed.SeedFilm.Id] });
        var byType = libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Trailer], Recursive = true });
        var all = libraryManager.GetItemList(new InternalItemsQuery { Recursive = true });

        Assert.Contains(byOwner, i => i.Id.Equals(seed.SeedTrailer.Id));
        Assert.DoesNotContain(byType, i => i.Id.Equals(seed.SeedTrailer.Id));
        Assert.DoesNotContain(all, i => i.Id.Equals(seed.SeedTrailer.Id));
        Assert.Contains(included, i => i.Id.Equals(seed.SeedTrailer.Id));
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return value;
    }

    private async Task<(HttpClient Client, Guid UserId)> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        var user = await AuthHelper.GetUserDtoAsync(client);
        return (client, user.Id);
    }
}
