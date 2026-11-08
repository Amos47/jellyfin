using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

public sealed class DiscoveryListsApiTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public DiscoveryListsApiTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    [Fact]
    public async Task PutList_CreatesVirtualFilms_HiddenFromItems()
    {
        var (client, userId) = await SetUpAsync();
        try
        {
            await PutListAsync(client, "matrix", (603, "tt0133093"));

            var film = Sync.FindFilm(603);
            Assert.NotNull(film);
            Assert.True(film.IsVirtualItem);
            Assert.True(string.IsNullOrEmpty(film.Path));
            Assert.Equal("TMDB 603", film.Name);
            Assert.Equal("603", film.GetProviderId(MetadataProvider.Tmdb));
            Assert.Equal("tt0133093", film.GetProviderId(MetadataProvider.Imdb));
            Assert.Equal(LibraryManager.GetNewItemId("discovery-tmdb-603", typeof(MediaBrowser.Controller.Entities.Movies.Movie)), film.Id);

            var items = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?recursive=true&userId={userId:N}");
            Assert.DoesNotContain(items.Items, i => i.Id.Equals(film.Id));

            var lists = await GetAsync<JsonArray>(client, "Discovery/Lists");
            var list = Assert.Single(lists, l => (string?)l!["Id"] == "matrix");
            Assert.Equal("The Matrix", (string?)list!["Title"]);
            Assert.Equal("tests", (string?)list["Source"]);
            var listed = Assert.Single(list["Films"]!.AsArray());
            Assert.Equal(603, (int)listed!["TmdbId"]!);
            Assert.Equal("tt0133093", (string?)listed["ImdbId"]);
        }
        finally
        {
            await DeleteListAsync(client, "matrix");
        }
    }

    [Fact]
    public void MetadataRefresher_IsNoOpInTests()
    {
        // Tests must make no network calls: the real refresher would queue TMDB lookups.
        var refresher = _factory.Services.GetRequiredService<IDiscoveryMetadataRefresher>();

        Assert.IsNotType<Emby.Server.Implementations.Discovery.DiscoveryMetadataRefresher>(refresher);
    }

    [Fact]
    public async Task PutList_NonAdmin_Forbidden()
    {
        var (client, _) = await SetUpAsync();
        var name = "regular-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("Users/New", new { Name = name, Password = "regular-pw" }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            create.EnsureSuccessStatusCode();
        }

        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, name, "regular-pw"));

        using var put = await SendPutAsync(userClient, "forbidden", (604, null));
        using var get = await userClient.GetAsync("Discovery/Lists", TestContext.Current.CancellationToken);
        using var delete = await userClient.DeleteAsync("Discovery/Lists/forbidden", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Null(Sync.FindFilm(604));
    }

    [Fact]
    public async Task PutList_InvalidId_BadRequest()
    {
        var (client, _) = await SetUpAsync();

        using var upper = await SendPutAsync(client, "Matrix", (605, null));
        using var tooLong = await SendPutAsync(client, new string('a', 65), (605, null));
        using var newline = await SendPutAsync(client, "matrix%0A", (605, null));
        using var deleteUpper = await client.DeleteAsync("Discovery/Lists/Matrix", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, upper.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, newline.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, deleteUpper.StatusCode);
        Assert.Null(Sync.FindFilm(605));
    }

    [Fact]
    public async Task PutList_DuplicateTmdbIds_Deduplicated()
    {
        var (client, _) = await SetUpAsync();
        try
        {
            await PutListAsync(client, "dupes", (606, null), (607, null), (606, "tt0000606"));

            var lists = await GetAsync<JsonArray>(client, "Discovery/Lists");
            var list = Assert.Single(lists, l => (string?)l!["Id"] == "dupes");
            var films = list!["Films"]!.AsArray().Select(f => (int)f!["TmdbId"]!).ToArray();
            Assert.Equal([606, 607], films);

            var discoveryFilms = GetDiscoveryFilms();
            Assert.Single(discoveryFilms, f => f.GetProviderId(MetadataProvider.Tmdb) == "606");
            Assert.Single(discoveryFilms, f => f.GetProviderId(MetadataProvider.Tmdb) == "607");
        }
        finally
        {
            await DeleteListAsync(client, "dupes");
        }
    }

    [Fact]
    public async Task DeleteList_RemovesOrphanFilms_KeepsSharedOnes()
    {
        var (client, _) = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        try
        {
            await PutListAsync(client, "first", (700, null), (701, null));
            await PutListAsync(client, "second", (701, null), (702, null));

            // A trailer extra of the film that is about to become an orphan.
            var orphan = Sync.FindFilm(700);
            Assert.NotNull(orphan);
            var trailer = new Trailer
            {
                Name = "Orphan Trailer",
                Path = Path.Combine(Path.GetTempPath(), "discovery-orphan-" + Guid.NewGuid().ToString("N")[..8] + "-trailer.mkv"),
                OwnerId = orphan.Id,
                ExtraType = ExtraType.Trailer,
            };
            trailer.Id = LibraryManager.GetNewItemId(trailer.Path, typeof(Trailer));
            LibraryManager.CreateItem(trailer, null);

            using (var delete = await client.DeleteAsync("Discovery/Lists/first", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            using (var again = await client.DeleteAsync("Discovery/Lists/first", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            }

            Assert.Null(Sync.FindFilm(700));
            Assert.Null(LibraryManager.GetItemById(orphan.Id));
            Assert.Null(LibraryManager.GetItemById(trailer.Id));
            Assert.Empty(LibraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, OwnerIds = [orphan.Id] }));
            Assert.NotNull(Sync.FindFilm(701));
            Assert.NotNull(Sync.FindFilm(702));

            // The seed film is in no list, so a sync removes it and its trailer as well.
            Assert.Null(LibraryManager.GetItemById(seed.SeedFilm.Id));
            Assert.Null(LibraryManager.GetItemById(seed.SeedTrailer.Id));
        }
        finally
        {
            await DeleteListAsync(client, "first");
            await DeleteListAsync(client, "second");
        }

        Assert.Null(Sync.FindFilm(701));
        Assert.Null(Sync.FindFilm(702));
    }

    [Fact]
    public async Task GetItem_DiscoveryFilm_Works()
    {
        var (client, userId) = await SetUpAsync();
        try
        {
            await PutListAsync(client, "detail", (708, null));
            var film = Sync.FindFilm(708);
            Assert.NotNull(film);

            var dto = await GetAsync<BaseItemDto>(client, $"Items/{film.Id:N}?userId={userId:N}");

            Assert.Equal(film.Id, dto.Id);
            Assert.Equal(LocationType.Virtual, dto.LocationType);
        }
        finally
        {
            await DeleteListAsync(client, "detail");
        }
    }

    [Fact]
    public async Task OwnedFilm_IsNeverTouched()
    {
        var (client, userId) = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var realFilm = LibraryManager.GetItemById(seed.RealFilm.Id);
        Assert.NotNull(realFilm);
        var path = realFilm.Path;
        realFilm.SetProviderId(MetadataProvider.Tmdb, "603");
        realFilm.SetProviderId(MetadataProvider.Imdb, "tt0133093");
        await realFilm.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
        var user = _factory.Services.GetRequiredService<IUserManager>().GetUserById(userId);
        Assert.NotNull(user);
        try
        {
            await PutListAsync(client, "owned", (603, "tt0133093"));
            Assert.NotNull(Sync.FindFilm(603));
            Assert.Equal(seed.RealFilm.Id, Sync.FindOwned(user, 603, null)?.Id);
            Assert.Equal(seed.RealFilm.Id, Sync.FindOwned(user, 999999, "tt0133093")?.Id);
            Assert.Null(Sync.FindOwned(user, 999999, null));

            await DeleteListAsync(client, "owned");
            Assert.Null(Sync.FindFilm(603));

            var after = LibraryManager.GetItemById(seed.RealFilm.Id);
            Assert.NotNull(after);
            Assert.False(after.IsVirtualItem);
            Assert.Equal(path, after.Path);
            Assert.Equal("603", after.GetProviderId(MetadataProvider.Tmdb));
            Assert.Equal(seed.RealFilm.Id, Sync.FindOwned(user, 603, null)?.Id);
        }
        finally
        {
            await DeleteListAsync(client, "owned");
            realFilm.ProviderIds.Remove(MetadataProvider.Tmdb.ToString());
            realFilm.ProviderIds.Remove(MetadataProvider.Imdb.ToString());
            await realFilm.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
        }
    }

    private BaseItem[] GetDiscoveryFilms()
        => LibraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeDiscovery = true,
            AncestorIds = [_factory.Services.GetRequiredService<IDiscoveryFolderLocator>().FolderId],
            IncludeItemTypes = [BaseItemKind.Movie],
        }).ToArray();

    private static Task<HttpResponseMessage> SendPutAsync(HttpClient client, string listId, params (int TmdbId, string? ImdbId)[] films)
        => client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new
            {
                Title = listId == "matrix" ? "The Matrix" : listId,
                Source = "tests",
                Films = films.Select(f => new { f.TmdbId, f.ImdbId }).ToArray(),
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

    private static async Task PutListAsync(HttpClient client, string listId, params (int TmdbId, string? ImdbId)[] films)
    {
        using var response = await SendPutAsync(client, listId, films);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task DeleteListAsync(HttpClient client, string listId)
    {
        using var response = await client.DeleteAsync("Discovery/Lists/" + listId, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"DELETE {listId}: {response.StatusCode}");
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return value;
    }

    private static async Task<string> AuthenticateAsync(HttpClient client, string username, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        request.Content = JsonContent.Create(new { Username = username, Pw = password }, options: JsonDefaults.Options);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        return (string)result!["AccessToken"]!;
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
