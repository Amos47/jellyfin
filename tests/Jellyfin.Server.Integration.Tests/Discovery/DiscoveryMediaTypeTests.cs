using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// The media type (Movie or Series) on lists, user lists, requests and Protected: Movie is the default everywhere,
/// and a film and a show with one TMDB id never meet. Its own host: it gives the seeded library film a TMDB id.
/// </summary>
public sealed class DiscoveryMediaTypeTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public DiscoveryMediaTypeTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-media-type-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IDiscoveryRequestStore Requests => _factory.Services.GetRequiredService<IDiscoveryRequestStore>();

    private IUserListStore UserLists => _factory.Services.GetRequiredService<IUserListStore>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_trailerRoot, true);
    }

    [Fact]
    public async Task Routes_DefaultToMovie()
    {
        var client = await SetUpAsync();
        var userId = await GetUserIdAsync(client);
        try
        {
            // A list without a type is a movie list, and its films get virtual movies.
            await PutListAsync(client, "mt-default", null, 7101);
            var list = await GetListAsync(client, "mt-default");
            Assert.Equal("Movie", (string?)list["ItemType"]);
            Assert.NotNull(Sync.FindFilm(7101));

            // Trailer routes: no type is the movie (found: the bad path is the error); a show is not found.
            Assert.Equal(HttpStatusCode.BadRequest, await PutTrailerAsync(client, "Discovery/Films/7101/Trailer"));
            Assert.Equal(HttpStatusCode.BadRequest, await PutTrailerAsync(client, "Discovery/Films/7101/Trailer?type=Movie"));
            Assert.Equal(HttpStatusCode.NotFound, await PutTrailerAsync(client, "Discovery/Films/7101/Trailer?type=Series"));
            using (var delete = await client.DeleteAsync("Discovery/Films/7101/Trailer?type=Series", Ct))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            // Requests: no type is the movie's request.
            await Requests.RequestAsync(7101, userId, Ct);
            Assert.Equal(HttpStatusCode.NoContent, await PutRequestAsync(client, "Discovery/Requests/7101", "downloading"));
            Assert.Equal(HttpStatusCode.NotFound, await PutRequestAsync(client, "Discovery/Requests/7101?type=Series", "downloading"));
            var request = await GetRequestAsync(client, 7101, "Movie");
            Assert.NotNull(request);
            Assert.Equal("downloading", (string?)request["State"]);
            Assert.Null(await GetRequestAsync(client, 7101, "Series"));

            // Untyped, the list is the movies' only: an app that knows no types never sees a show's request.
            await Requests.RequestAsync(7102, DiscoveryMediaType.Series, userId, Ct);
            var untyped = await GetAsync<JsonArray>(client, "Discovery/Requests");
            Assert.Contains(untyped, r => (int)r!["TmdbId"]! == 7101);
            Assert.All(untyped, r => Assert.Equal("Movie", (string?)r!["Type"]));
            Assert.NotNull(await GetRequestAsync(client, 7102, "Series"));
            await Requests.SetStateAsync(7102, DiscoveryMediaType.Series, DiscoveryRequest.StateDone, Ct);

            // User lists: no type adds and removes the movie; the server knows no show 7101 yet.
            var saved = (string)(await GetSavedAsync(client))["Id"]!;
            var added = await SendAsync<JsonObject>(client, HttpMethod.Post, $"Discovery/UserLists/{saved}/Films/7101");
            var film = Assert.Single(added["Films"]!.AsArray())!;
            Assert.Equal(7101, (int)film["TmdbId"]!);
            Assert.Equal("Movie", (string?)film["Type"]);
            using (var post = await client.PostAsync($"Discovery/UserLists/{saved}/Films/7101?type=Series", null, Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            }

            Assert.Single((await SendAsync<JsonObject>(client, HttpMethod.Delete, $"Discovery/UserLists/{saved}/Films/7101?type=Series"))["Films"]!.AsArray());
            Assert.Empty((await SendAsync<JsonObject>(client, HttpMethod.Delete, $"Discovery/UserLists/{saved}/Films/7101"))["Films"]!.AsArray());

            // An unknown type is a bad request.
            using (var bogus = await client.DeleteAsync($"Discovery/UserLists/{saved}/Films/7101?type=Episode", Ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
            }

            using (var bogusList = await client.PutAsJsonAsync(
                "Discovery/Lists/mt-bogus",
                new { Title = "Bogus", Source = "tests", Films = new[] { new { TmdbId = 1 } }, ItemType = "Episode" },
                JsonDefaults.Options,
                Ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, bogusList.StatusCode);
            }

            // A number that names no type is refused too, not stored.
            using (var numericList = await client.PutAsJsonAsync(
                "Discovery/Lists/mt-bogus",
                new { Title = "Bogus", Source = "tests", Films = new[] { new { TmdbId = 1 } }, ItemType = 7 },
                JsonDefaults.Options,
                Ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, numericList.StatusCode);
            }

            Assert.DoesNotContain(await GetAsync<JsonArray>(client, "Discovery/Lists"), l => (string?)l!["Id"] == "mt-bogus");
        }
        finally
        {
            await DeleteListsAsync(client, "mt-default", "mt-bogus");
        }
    }

    [Fact]
    public async Task Protected_ItemsCarryType()
    {
        var client = await SetUpAsync();
        var userId = await GetUserIdAsync(client);
        var saved = await UserLists.GetDefaultListAsync(userId, Ct);

        await Requests.RequestAsync(7201, userId, Ct);
        await Requests.RequestAsync(7202, DiscoveryMediaType.Series, userId, Ct);
        await UserLists.AddFilmAsync(userId, saved.Id, 7203, DiscoveryMediaType.Series, null, Ct);
        try
        {
            var protectedFilms = await GetAsync<JsonObject>(client, "Discovery/Protected");

            // TmdbIds stays the movies only, for apps that know no types.
            var tmdbIds = protectedFilms["TmdbIds"]!.AsArray().Select(i => (int)i!).ToArray();
            Assert.Contains(7201, tmdbIds);
            Assert.DoesNotContain(7202, tmdbIds);
            Assert.DoesNotContain(7203, tmdbIds);

            var items = ProtectedItems(protectedFilms);
            Assert.Contains((7201, "Movie"), items);
            Assert.Contains((7202, "Series"), items);
            Assert.Contains((7203, "Series"), items);
            Assert.DoesNotContain((7201, "Series"), items);
            Assert.DoesNotContain((7202, "Movie"), items);

            // Movies first, then shows, each by TMDB id.
            Assert.Equal(items.OrderBy(i => i.Type == "Series").ThenBy(i => i.TmdbId), items);
        }
        finally
        {
            await Requests.SetStateAsync(7201, DiscoveryRequest.StateDone, Ct);
            await Requests.SetStateAsync(7202, DiscoveryMediaType.Series, DiscoveryRequest.StateDone, Ct);
            await UserLists.RemoveFilmAsync(userId, saved.Id, 7203, DiscoveryMediaType.Series, Ct);
        }
    }

    [Fact]
    public async Task SameTmdbId_MovieAndSeries_DoNotCollide()
    {
        var client = await SetUpAsync();
        var userId = await GetUserIdAsync(client);
        try
        {
            // Lists: a movie list and a show list with one TMDB id, each typed.
            await PutListAsync(client, "mt-films", null, 7301);
            await PutListAsync(client, "mt-shows", "Series", 7301);
            Assert.Equal("Movie", (string?)(await GetListAsync(client, "mt-films"))["ItemType"]);
            Assert.Equal("Series", (string?)(await GetListAsync(client, "mt-shows"))["ItemType"]);

            // The show list never keeps a virtual movie: without the movie list, the film goes.
            Assert.NotNull(Sync.FindFilm(7301));
            await DeleteListsAsync(client, "mt-films");
            Assert.Null(Sync.FindFilm(7301));
            await PutListAsync(client, "mt-films", null, 7301);
            var filmId = Sync.FindFilm(7301)!.Id;

            // The show list's page shows no movie of that id.
            Assert.Empty((await GetAsync<JsonObject>(client, "Discovery/Lists/mt-shows/Page"))["Items"]!.AsArray());
            Assert.Equal(0, (int)(await GetListAsync(client, "mt-shows"))["ReadyFilms"]!);

            // Requests: one per type, each moving on its own.
            await Requests.RequestAsync(7301, userId, Ct);
            await Requests.RequestAsync(7301, DiscoveryMediaType.Series, userId, Ct);
            Assert.Equal(HttpStatusCode.NoContent, await PutRequestAsync(client, "Discovery/Requests/7301?type=Series", "downloading"));
            Assert.Equal("requested", (string?)(await GetRequestAsync(client, 7301, "Movie"))!["State"]);
            Assert.Equal("downloading", (string?)(await GetRequestAsync(client, 7301, "Series"))!["State"]);
            var seriesOnly = await GetAsync<JsonArray>(client, "Discovery/Requests?type=Series");
            Assert.All(seriesOnly, r => Assert.Equal("Series", (string?)r!["Type"]));

            // User lists: Saved holds both; removing the show leaves the film.
            var saved = await UserLists.GetDefaultListAsync(userId, Ct);
            await SendAsync<JsonObject>(client, HttpMethod.Post, $"Discovery/UserLists/{saved.Id:N}/Films/7301");
            await UserLists.AddFilmAsync(userId, saved.Id, 7301, DiscoveryMediaType.Series, null, Ct);
            var both = (await GetSavedAsync(client))["Films"]!.AsArray();
            Assert.Equal(new[] { "Movie", "Series" }, both.Select(f => (string)f!["Type"]!).Order());
            Assert.Equal("on", (string?)FindAction(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions"), "save")["State"]);

            // Protected: the id once in TmdbIds (the movie), twice in Items.
            var protectedFilms = await GetAsync<JsonObject>(client, "Discovery/Protected");
            Assert.Single(protectedFilms["TmdbIds"]!.AsArray(), i => (int)i! == 7301);
            Assert.Contains((7301, "Movie"), ProtectedItems(protectedFilms));
            Assert.Contains((7301, "Series"), ProtectedItems(protectedFilms));

            // The film arriving in the library finishes the movie's request only.
            await SetOwnedTmdbAsync(7301);
            Assert.Equal("done", (string?)(await GetRequestAsync(client, 7301, "Movie"))!["State"]);
            Assert.Equal("downloading", (string?)(await GetRequestAsync(client, 7301, "Series"))!["State"]);
            var afterDone = ProtectedItems(await GetAsync<JsonObject>(client, "Discovery/Protected"));
            Assert.Contains((7301, "Series"), afterDone);

            var afterShowRemoved = await SendAsync<JsonObject>(client, HttpMethod.Delete, $"Discovery/UserLists/{saved.Id:N}/Films/7301?type=Series");
            var left = Assert.Single(afterShowRemoved["Films"]!.AsArray())!;
            Assert.Equal("Movie", (string?)left["Type"]);
        }
        finally
        {
            await SetOwnedTmdbAsync(null);
            await Requests.SetStateAsync(7301, DiscoveryMediaType.Series, DiscoveryRequest.StateDone, Ct);
            var saved = await UserLists.GetDefaultListAsync(userId, Ct);
            await UserLists.RemoveFilmAsync(userId, saved.Id, 7301, Ct);
            await UserLists.RemoveFilmAsync(userId, saved.Id, 7301, DiscoveryMediaType.Series, Ct);
            await DeleteListsAsync(client, "mt-films", "mt-shows");
        }
    }

    private static (int TmdbId, string Type)[] ProtectedItems(JsonObject protectedFilms)
        => protectedFilms["Items"]!.AsArray().Select(i => ((int)i!["TmdbId"]!, (string)i["Type"]!)).ToArray();

    private static JsonObject FindAction(JsonArray actions, string id)
        => actions.Single(a => (string?)a!["Id"] == id)!.AsObject();

    private static async Task<JsonObject> GetListAsync(HttpClient client, string listId)
        => (await GetAsync<JsonArray>(client, "Discovery/Lists")).Single(l => (string?)l!["Id"] == listId)!.AsObject();

    private static async Task<JsonObject> GetSavedAsync(HttpClient client)
        => (await GetAsync<JsonArray>(client, "Discovery/UserLists")).Single(l => (bool)l!["IsDefault"]!)!.AsObject();

    private static async Task<JsonObject?> GetRequestAsync(HttpClient client, int tmdbId, string type)
        => (await GetAsync<JsonArray>(client, "Discovery/Requests?type=" + type))
            .SingleOrDefault(r => (int)r!["TmdbId"]! == tmdbId && (string?)r["Type"] == type)?.AsObject();

    private static async Task<HttpStatusCode> PutRequestAsync(HttpClient client, string url, string state)
    {
        using var response = await client.PutAsJsonAsync(url, new { State = state }, JsonDefaults.Options, Ct);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> PutTrailerAsync(HttpClient client, string url)
    {
        // A path inside the trailer folder that isn't a file: the movie is found, the path is refused.
        using var response = await client.PutAsJsonAsync(url, new { Path = Path.Combine(_trailerRoot, "missing.mp4") }, JsonDefaults.Options, Ct);
        return response.StatusCode;
    }

    private static async Task<Guid> GetUserIdAsync(HttpClient client)
        => Guid.Parse((string)(await GetAsync<JsonObject>(client, "Users/Me"))["Id"]!);

    // The seeded library film, given (or cleared of) a TMDB id.
    private async Task SetOwnedTmdbAsync(int? tmdbId)
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var film = LibraryManager.GetItemById(seed.RealFilm.Id)!;
        if (tmdbId is { } id)
        {
            film.SetProviderId(MetadataProvider.Tmdb, id.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            film.ProviderIds.Remove(MetadataProvider.Tmdb.ToString());
        }

        await film.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
    }

    private void SetTrailerRoot(string? root)
        => _factory.Services.GetRequiredService<IConfigurationManager>()
            .SaveConfiguration("discovery", new DiscoveryOptions { TrailerRoot = root });

    private static async Task PutListAsync(HttpClient client, string listId, string? itemType, params int[] tmdbIds)
    {
        var films = tmdbIds.Select(t => new { TmdbId = t }).ToArray();
        using var response = itemType is null
            ? await client.PutAsJsonAsync("Discovery/Lists/" + listId, new { Title = listId, Source = "tests", Films = films }, JsonDefaults.Options, Ct)
            : await client.PutAsJsonAsync("Discovery/Lists/" + listId, new { Title = listId, Source = "tests", Films = films, ItemType = itemType }, JsonDefaults.Options, Ct);
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
        => await SendAsync<T>(client, HttpMethod.Get, url);

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url}: {response.StatusCode}");
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, Ct);
        Assert.NotNull(value);
        return value;
    }

    private async Task<HttpClient> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        return client;
    }
}
