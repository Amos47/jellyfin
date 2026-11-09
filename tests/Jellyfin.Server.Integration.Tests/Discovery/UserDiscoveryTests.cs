using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.HomeRows.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// The user-level discovery routes: a list's page, its actions and their queue, and the film queue with a list context.
/// </summary>
public sealed class UserDiscoveryTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public UserDiscoveryTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-user-discovery-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private IHomeRowConfigurationService Layouts => _factory.Services.GetRequiredService<IHomeRowConfigurationService>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_trailerRoot, true);
    }

    [Fact]
    public async Task ListPage_ReturnsPlayableFilmsInOrder_AndImages()
    {
        var client = await SetUpAsync();
        try
        {
            // 2101 has no trailer (left out); 2102 has no image; 2103 a poster; 2104 a backdrop and a poster.
            await PutListAsync(client, "lp-1", "Page Title", 2101, 2102, 2103, 2104);
            await SetTrailerAsync(client, 2102, "page-2102");
            await SetTrailerAsync(client, 2103, "page-2103");
            await SetTrailerAsync(client, 2104, "page-2104");
            await SetImageAsync(2103, ImageType.Primary);
            await SetImageAsync(2104, ImageType.Backdrop);
            await SetImageAsync(2104, ImageType.Primary);

            var page = await GetAsync<JsonObject>(client, "Discovery/Lists/lp-1/Page");

            Assert.Equal("lp-1", (string?)page["Id"]);
            Assert.Equal("Page Title", (string?)page["Title"]);
            Assert.Equal(
                new[] { Sync.FindFilm(2102)!.Id, Sync.FindFilm(2103)!.Id, Sync.FindFilm(2104)!.Id },
                page["Items"]!.AsArray().Select(n => Guid.Parse((string)n!["Id"]!)).ToArray());
            Assert.Equal(Sync.FindFilm(2103)!.Id, Guid.Parse((string)page["PrimaryItemId"]!));
            Assert.Equal(Sync.FindFilm(2104)!.Id, Guid.Parse((string)page["BackdropItemId"]!));
        }
        finally
        {
            await DeleteListsAsync(client, "lp-1");
        }
    }

    [Fact]
    public async Task ListPage_NonAdminAllowed()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await PutListAsync(client, "lp-2", "Shared List", 2111, 2112);
            var first = await SetTrailerAsync(client, 2111, "page-2111");
            var second = await SetTrailerAsync(client, 2112, "page-2112");

            var page = await GetAsync<JsonObject>(userClient, "Discovery/Lists/lp-2/Page");
            var actions = await GetAsync<JsonArray>(userClient, "Discovery/Lists/lp-2/Actions");
            var queue = await GetAsync<JsonObject>(userClient, "Discovery/Lists/lp-2/Actions/watch-trailers/Queue");

            Assert.Equal(2, page["Items"]!.AsArray().Count);
            Assert.Equal("watch-trailers", (string?)Assert.Single(actions)!["Id"]);
            Assert.Equal(new[] { first, second }, QueueIds(queue));
        }
        finally
        {
            await UserManager.DeleteUserAsync(user.Id);
            await DeleteListsAsync(client, "lp-2");
        }
    }

    [Fact]
    public async Task ListPage_UnknownIs404()
    {
        var client = await SetUpAsync();
        await PutListAsync(client, "lp-3", "No Trailers", 2121);
        try
        {
            using var page = await client.GetAsync("Discovery/Lists/no-such-list/Page", Ct);
            using var actions = await client.GetAsync("Discovery/Lists/no-such-list/Actions", Ct);
            using var queue = await client.GetAsync("Discovery/Lists/no-such-list/Actions/watch-trailers/Queue", Ct);
            using var unknownAction = await client.GetAsync("Discovery/Lists/lp-3/Actions/no-such-action/Queue", Ct);

            // A list with nothing to play has no Watch trailers, so its queue is a 404 too.
            using var emptyQueue = await client.GetAsync("Discovery/Lists/lp-3/Actions/watch-trailers/Queue", Ct);
            var emptyActions = await GetAsync<JsonArray>(client, "Discovery/Lists/lp-3/Actions");
            var emptyPage = await GetAsync<JsonObject>(client, "Discovery/Lists/lp-3/Page");

            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, actions.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, queue.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, unknownAction.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, emptyQueue.StatusCode);
            Assert.Empty(emptyActions);
            Assert.Empty(emptyPage["Items"]!.AsArray());
            Assert.Null(emptyPage["PrimaryItemId"]);
            Assert.Null(emptyPage["BackdropItemId"]);
        }
        finally
        {
            await DeleteListsAsync(client, "lp-3");
        }
    }

    [Fact]
    public async Task ListActions_WatchTrailers_QueueIsWholeList()
    {
        var client = await SetUpAsync();
        try
        {
            // Every playable film, in list order; 2132 has no trailer and is skipped.
            await PutListAsync(client, "la-1", "Action List", 2131, 2132, 2133);
            var first = await SetTrailerAsync(client, 2131, "list-2131");
            var last = await SetTrailerAsync(client, 2133, "list-2133");

            var actions = await GetAsync<JsonArray>(client, "Discovery/Lists/la-1/Actions");
            var queue = await GetAsync<JsonObject>(client, "Discovery/Lists/la-1/Actions/watch-trailers/Queue");

            var action = Assert.Single(actions)!;
            Assert.Equal("watch-trailers", (string?)action["Id"]);
            Assert.Equal("Watch trailers", (string?)action["Label"]);
            Assert.Equal("play-queue", (string?)action["Kind"]);
            Assert.Equal(new[] { first, last }, QueueIds(queue));
            var items = queue["Items"]!.AsArray();
            Assert.Equal(new[] { first, last }, items.Select(n => Guid.Parse((string)n!["Id"]!)).ToArray());
            Assert.Equal(
                new[] { Sync.FindFilm(2131)!.Id, Sync.FindFilm(2133)!.Id },
                items.Select(n => Guid.Parse((string)n!["FilmId"]!)).ToArray());
            Assert.Equal(Sync.FindFilm(2131)!.Name, (string?)items[0]!["Title"]);
        }
        finally
        {
            await DeleteListsAsync(client, "la-1");
        }
    }

    [Fact]
    public async Task FilmQueue_WithListId_ContinuesThroughThatList()
    {
        var client = await SetUpAsync();
        var admin = UserManager.GetUserById((await AuthHelper.GetUserDtoAsync(client)).Id)!;

        // No Movie discovery row: the list is not one of today's picks, only the list context links the films.
        await Layouts.ResetUserLayoutAsync(admin, Ct);
        try
        {
            await PutListAsync(client, "fq-1", "Context List", 2141, 2142, 2143);
            var before = await SetTrailerAsync(client, 2141, "context-2141");
            var own = await SetTrailerAsync(client, 2142, "context-2142");
            var after = await SetTrailerAsync(client, 2143, "context-2143");
            var filmId = Sync.FindFilm(2142)!.Id;

            var withList = await GetAsync<JsonObject>(client, $"Items/{filmId:N}/Actions/watch-trailer/Queue?listId=fq-1");
            var withoutList = await GetAsync<JsonObject>(client, $"Items/{filmId:N}/Actions/watch-trailer/Queue");
            var unknownList = await GetAsync<JsonObject>(client, $"Items/{filmId:N}/Actions/watch-trailer/Queue?listId=no-such-list");

            Assert.Equal(new[] { own, after }, QueueIds(withList));
            Assert.DoesNotContain(before, QueueIds(withList));
            Assert.Equal(new[] { own }, QueueIds(withoutList));
            Assert.Equal(new[] { own }, QueueIds(unknownList));
        }
        finally
        {
            await DeleteListsAsync(client, "fq-1");
        }
    }

    [Fact]
    public async Task FilmQueue_WithListIdOfAnotherList_KeepsCurrentBehaviour()
    {
        var client = await SetUpAsync();
        var admin = UserManager.GetUserById((await AuthHelper.GetUserDtoAsync(client)).Id)!;
        await Layouts.ResetUserLayoutAsync(admin, Ct);
        try
        {
            await PutListAsync(client, "fq-2", "Film List", 2151);
            await PutListAsync(client, "fq-3", "Other List", 2152, 2153);
            var own = await SetTrailerAsync(client, 2151, "context-2151");
            await SetTrailerAsync(client, 2152, "context-2152");
            await SetTrailerAsync(client, 2153, "context-2153");

            var queue = await GetAsync<JsonObject>(client, $"Items/{Sync.FindFilm(2151)!.Id:N}/Actions/watch-trailer/Queue?listId=fq-3");

            Assert.Equal(new[] { own }, QueueIds(queue));
        }
        finally
        {
            await DeleteListsAsync(client, "fq-2", "fq-3");
        }
    }

    private static Guid[] QueueIds(JsonObject queue)
        => queue["ItemIds"]!.AsArray().Select(n => Guid.Parse((string)n!)).ToArray();

    private async Task<Guid> SetTrailerAsync(HttpClient client, int tmdbId, string name)
    {
        var path = Path.Combine(_trailerRoot, name + "-trailer.mkv");
        await File.WriteAllBytesAsync(path, new byte[1024], Ct);
        using (var response = await client.PutAsJsonAsync($"Discovery/Films/{tmdbId}/Trailer", new { Path = path }, JsonDefaults.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var film = Sync.FindFilm(tmdbId)!;
        return Assert.Single(film.GetExtras([ExtraType.Trailer])).Id;
    }

    private async Task SetImageAsync(int tmdbId, ImageType type)
    {
        var film = Sync.FindFilm(tmdbId)!;
        var path = Path.Combine(_trailerRoot, $"{tmdbId}-{type}.jpg");
        await File.WriteAllBytesAsync(path, new byte[16], Ct);
        film.SetImage(new ItemImageInfo { Path = path, Type = type, DateModified = DateTime.UtcNow }, 0);
        await film.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
    }

    private async Task<(User User, HttpClient Client)> CreateNonAdminAsync()
    {
        var client = await SetUpAsync();
        var name = "lists-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("Users/New", new { Name = name, Password = "lists-pw" }, JsonDefaults.Options, Ct))
        {
            create.EnsureSuccessStatusCode();
        }

        var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, name, "lists-pw"));
        return (UserManager.GetUserByName(name)!, userClient);
    }

    private void SetTrailerRoot(string? root)
        => _factory.Services.GetRequiredService<IConfigurationManager>()
            .SaveConfiguration("discovery", new DiscoveryOptions { TrailerRoot = root });

    private static async Task PutListAsync(HttpClient client, string listId, string title, params int[] tmdbIds)
    {
        using var response = await client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new { Title = title, Source = "tests", Films = tmdbIds.Select(t => new { TmdbId = t }).ToArray() },
            JsonDefaults.Options,
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task DeleteListsAsync(HttpClient client, params string[] listIds)
    {
        foreach (var listId in listIds)
        {
            using var response = await client.DeleteAsync("Discovery/Lists/" + listId, Ct);
            Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"DELETE {listId}: {response.StatusCode}");
        }
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {response.StatusCode}");
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, Ct);
        Assert.NotNull(value);
        return value;
    }

    private static async Task<string> AuthenticateAsync(HttpClient client, string username, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        request.Content = JsonContent.Create(new { Username = username, Pw = password }, options: JsonDefaults.Options);
        using var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
        return (string)result!["AccessToken"]!;
    }

    private async Task<HttpClient> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        return client;
    }
}
