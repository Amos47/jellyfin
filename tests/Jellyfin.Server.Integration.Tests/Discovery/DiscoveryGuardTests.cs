using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
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

        using var response = await client.GetAsync($"Items?parentId={seed.FolderId:N}&userId={userId:N}", TestContext.Current.CancellationToken);

        // Hidden folder: either refused outright or empty, never the virtual film.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Empty(result.Items);
        }
        else
        {
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, response.StatusCode.ToString());
        }

        using var recursive = await client.GetAsync($"Items?parentId={seed.FolderId:N}&recursive=true&userId={userId:N}", TestContext.Current.CancellationToken);
        if (recursive.StatusCode == HttpStatusCode.OK)
        {
            var result = await recursive.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Empty(result.Items);
        }
    }

    [Fact]
    public async Task Items_ByIds_ExcludesDiscovery()
    {
        var (client, userId) = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);

        var result = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?ids={seed.SeedFilm.Id:N},{seed.RealFilm.Id:N}&userId={userId:N}");

        Assert.Contains(result.Items, i => i.Id.Equals(seed.RealFilm.Id));
        Assert.DoesNotContain(result.Items, i => i.Id.Equals(seed.SeedFilm.Id));
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
    public async Task Repository_ExtraOfDiscoveryFilm_ExcludedUnlessIncluded()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        var hidden = libraryManager.GetItemList(new InternalItemsQuery { OwnerIds = [seed.SeedFilm.Id] });
        var included = libraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, OwnerIds = [seed.SeedFilm.Id] });
        var byType = libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Trailer], Recursive = true });

        Assert.DoesNotContain(hidden, i => i.Id.Equals(seed.SeedTrailer.Id));
        Assert.DoesNotContain(byType, i => i.Id.Equals(seed.SeedTrailer.Id));
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
