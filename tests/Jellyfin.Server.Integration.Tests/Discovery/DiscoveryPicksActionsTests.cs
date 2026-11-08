using System;
using System.Collections.Generic;
using System.Globalization;
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
using Jellyfin.HomeRows;
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
/// The admin picks and films API, and the Watch trailer item action with its queue.
/// </summary>
public sealed class DiscoveryPicksActionsTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public DiscoveryPicksActionsTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-discovery-picks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private IHomeRowConfigurationService Layouts => _factory.Services.GetRequiredService<IHomeRowConfigurationService>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    private IDiscoveryRowPlanner Planner => _factory.Services.GetRequiredService<IDiscoveryRowPlanner>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_trailerRoot, true);
    }

    [Fact]
    public async Task Picks_UnionAcrossEnabledUsers_IgnoresDisabled()
    {
        var client = await SetUpAsync();
        var a = await CreateUserAsync("pick-a");
        var b = await CreateUserAsync("pick-b");
        var off = await CreateUserAsync("pick-off");
        try
        {
            await PutListAsync(client, "pk-1", 1101, 1102);
            await PutListAsync(client, "pk-2", 1103);
            await PutListAsync(client, "pk-3", 1104, 1105);
            await SaveLayoutAsync(a, maxRows: 1, enabled: true);
            await SaveLayoutAsync(b, maxRows: 1, enabled: true);
            await SaveLayoutAsync(off, maxRows: 3, enabled: false);
            var date = new DateOnly(2031, 3, 14);

            var picks = await GetAsync<JsonObject>(client, "Discovery/Picks?date=2031-03-14");

            var expected = new HashSet<int>();
            foreach (var user in new[] { a, b })
            {
                foreach (var list in await Planner.GetPickedListsAsync(user, 1, date, Ct))
                {
                    expected.UnionWith(list.Films.Select(f => f.TmdbId));
                }
            }

            var tmdbIds = picks["TmdbIds"]!.AsArray().Select(n => (int)n!).ToArray();
            Assert.Equal("2031-03-14", (string?)picks["Date"]);
            Assert.Equal(expected.Order(), tmdbIds.Order());
            Assert.Equal(tmdbIds.Length, tmdbIds.Distinct().Count());

            // At most two of the three lists: the disabled row (all three) adds nothing.
            Assert.True(tmdbIds.Length < 5, string.Join(",", tmdbIds));

            // Picks are a forecast, not a showing.
            var shown = await _factory.Services.GetRequiredService<IDiscoveryStore>().GetShownAsync(Ct);
            Assert.DoesNotContain(shown.Keys, k => k is >= 1101 and <= 1105);
        }
        finally
        {
            await DeleteUsersAsync(a, b, off);
            await DeleteListsAsync(client, "pk-1", "pk-2", "pk-3");
        }
    }

    [Fact]
    public async Task Picks_DefaultsToTomorrow()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("pick-tomorrow");
        try
        {
            await PutListAsync(client, "pk-t1", 1111);
            await PutListAsync(client, "pk-t2", 1112);
            await PutListAsync(client, "pk-t3", 1113);
            await SaveLayoutAsync(user, maxRows: 1, enabled: true);
            var tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1);

            var picks = await GetAsync<JsonObject>(client, "Discovery/Picks");
            var explicitPicks = await GetAsync<JsonObject>(client, $"Discovery/Picks?date={tomorrow:yyyy-MM-dd}");

            Assert.Equal(tomorrow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), (string?)picks["Date"]);
            var expected = (await Planner.GetPickedListsAsync(user, 1, tomorrow, Ct)).SelectMany(l => l.Films).Select(f => f.TmdbId);
            Assert.Equal(expected.Order(), picks["TmdbIds"]!.AsArray().Select(n => (int)n!).Order());
            Assert.Equal(
                explicitPicks["TmdbIds"]!.AsArray().Select(n => (int)n!),
                picks["TmdbIds"]!.AsArray().Select(n => (int)n!));
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, "pk-t1", "pk-t2", "pk-t3");
        }
    }

    [Fact]
    public async Task Films_ReportsTrailerAndShown()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "fl-1", 1121, 1122);
            await SetTrailerAsync(client, 1121, "films-1121");
            var day = new DateOnly(2030, 1, 2);
            var store = _factory.Services.GetRequiredService<IDiscoveryStore>();
            await store.MarkShownAsync([1121], day, Ct);
            await store.MarkShownAsync([1121], day.AddDays(3), Ct);

            var films = await GetAsync<JsonArray>(client, "Discovery/Films");

            var withTrailer = Assert.Single(films, f => (int)f!["TmdbId"]! == 1121)!;
            Assert.Equal(Sync.FindFilm(1121)!.Id, Guid.Parse((string)withTrailer["ItemId"]!));
            Assert.Equal(Sync.FindFilm(1121)!.Name, (string?)withTrailer["Title"]);
            Assert.True((bool)withTrailer["HasTrailer"]!);
            Assert.Equal("2030-01-02", (string?)withTrailer["FirstShown"]);
            Assert.Equal("2030-01-05", (string?)withTrailer["LastShown"]);

            var plain = Assert.Single(films, f => (int)f!["TmdbId"]! == 1122)!;
            Assert.Equal(Sync.FindFilm(1122)!.Id, Guid.Parse((string)plain["ItemId"]!));
            Assert.False((bool)plain["HasTrailer"]!);
            Assert.Null(plain["FirstShown"]);
            Assert.Null(plain["LastShown"]);
        }
        finally
        {
            await DeleteListsAsync(client, "fl-1");
        }
    }

    [Fact]
    public async Task Actions_FilmWithTrailer_HasWatchTrailer()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "ac-1", 1131);
            await SetTrailerAsync(client, 1131, "actions-1131");

            var actions = await GetAsync<JsonArray>(client, $"Items/{Sync.FindFilm(1131)!.Id:N}/Actions");

            var action = Assert.Single(actions)!;
            Assert.Equal("watch-trailer", (string?)action["Id"]);
            Assert.Equal("Watch trailer", (string?)action["Label"]);
            Assert.Equal("trailer", (string?)action["Icon"]);
            Assert.Equal("play-queue", (string?)action["Kind"]);
        }
        finally
        {
            await DeleteListsAsync(client, "ac-1");
        }
    }

    [Fact]
    public async Task Actions_FilmWithoutTrailer_Empty()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "ac-2", 1141);
            var filmId = Sync.FindFilm(1141)!.Id;

            var actions = await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions");
            using var queue = await client.GetAsync($"Items/{filmId:N}/Actions/watch-trailer/Queue", Ct);
            using var missing = await client.GetAsync($"Items/{Guid.NewGuid():N}/Actions", Ct);

            Assert.Empty(actions);
            Assert.Equal(HttpStatusCode.NotFound, queue.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await DeleteListsAsync(client, "ac-2");
        }
    }

    [Fact]
    public async Task Queue_FromPickedList_ContinuesInOrderSkippingNoTrailer()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        try
        {
            // Before the film (left out), the film, one without a trailer (skipped), one after it.
            await PutListAsync(client, "qu-1", 1151, 1152, 1153, 1154);
            var before = await SetTrailerAsync(client, 1151, "queue-1151");
            var own = await SetTrailerAsync(client, 1152, "queue-1152");
            var after = await SetTrailerAsync(client, 1154, "queue-1154");
            await SaveLayoutAsync(admin, maxRows: 10, enabled: true);

            var queue = await GetQueueAsync(client, Sync.FindFilm(1152)!.Id);

            Assert.Equal(new[] { own, after }, queue);
            Assert.DoesNotContain(before, queue);
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, Ct);
            await DeleteListsAsync(client, "qu-1");
        }
    }

    [Fact]
    public async Task Queue_NotInPick_OwnTrailersOnly()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        await Layouts.ResetUserLayoutAsync(admin, Ct);
        try
        {
            await PutListAsync(client, "qu-2", 1161, 1162);
            var own = await SetTrailerAsync(client, 1161, "queue-1161");
            await SetTrailerAsync(client, 1162, "queue-1162");

            var queue = await GetQueueAsync(client, Sync.FindFilm(1161)!.Id);

            Assert.Equal(new[] { own }, queue);
        }
        finally
        {
            await DeleteListsAsync(client, "qu-2");
        }
    }

    [Fact]
    public async Task Actions_OwnedFilmWithoutTrailer_UsesDiscoveryCopyTrailer()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        await Layouts.ResetUserLayoutAsync(admin, Ct);
        var owned = await SetOwnedTmdbAsync(1171);
        try
        {
            await PutListAsync(client, "ow-1", 1171);
            var copyTrailer = await SetTrailerAsync(client, 1171, "owned-1171");

            var actions = await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions");
            var queue = await GetQueueAsync(client, owned.Id);

            var action = Assert.Single(actions)!;
            Assert.Equal("watch-trailer", (string?)action["Id"]);
            Assert.Equal("Watch trailer", (string?)action["Label"]);
            Assert.Equal(new[] { copyTrailer }, queue);
        }
        finally
        {
            await DeleteListsAsync(client, "ow-1");
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task Actions_OwnedFilmWithOnlyImdbId_UsesDiscoveryCopyTrailer()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        await Layouts.ResetUserLayoutAsync(admin, Ct);
        // The owned film has no TMDB id, only an IMDb id; its discovery copy has both.
        var owned = await SetOwnedImdbAsync("tt9001176");
        try
        {
            using (var put = await client.PutAsJsonAsync(
                "Discovery/Lists/ow-imdb",
                new { Title = "ow-imdb", Source = "tests", Films = new[] { new { TmdbId = 1176, ImdbId = "tt9001176" } } },
                JsonDefaults.Options,
                Ct))
            {
                Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            }

            var copyTrailer = await SetTrailerAsync(client, 1176, "owned-imdb-1176");

            var actions = await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions");
            var queue = await GetQueueAsync(client, owned.Id);

            var action = Assert.Single(actions)!;
            Assert.Equal("watch-trailer", (string?)action["Id"]);
            Assert.Equal(new[] { copyTrailer }, queue);
        }
        finally
        {
            await DeleteListsAsync(client, "ow-imdb");
            await SetOwnedImdbAsync(null);
        }
    }

    [Fact]
    public async Task Actions_OwnedFilmWithOwnTrailer_PrefersOwn()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        await Layouts.ResetUserLayoutAsync(admin, Ct);
        var owned = await SetOwnedTmdbAsync(1172);
        var ownTrailer = AddOwnTrailer(owned);
        try
        {
            await PutListAsync(client, "ow-2", 1172);
            var copyTrailer = await SetTrailerAsync(client, 1172, "owned-1172");

            var actions = await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions");
            var queue = await GetQueueAsync(client, owned.Id);

            Assert.Equal("watch-trailer", (string?)Assert.Single(actions)!["Id"]);
            Assert.Equal(new[] { ownTrailer.Id }, queue);
            Assert.DoesNotContain(copyTrailer, queue);
        }
        finally
        {
            LibraryManager.DeleteItem(ownTrailer, new DeleteOptions { DeleteFileLocation = false });
            await DeleteListsAsync(client, "ow-2");
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task Queue_OwnedFilmInRow_UsesDiscoveryCopyTrailer()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var owned = await SetOwnedTmdbAsync(1182);
        try
        {
            // A discovery film, the owned film (no trailer of its own), another discovery film.
            await PutListAsync(client, "ow-3", 1181, 1182, 1183);
            var first = await SetTrailerAsync(client, 1181, "owned-row-1181");
            var copy = await SetTrailerAsync(client, 1182, "owned-row-1182");
            var last = await SetTrailerAsync(client, 1183, "owned-row-1183");
            await SaveLayoutAsync(admin, maxRows: 10, enabled: true);
            Assert.Contains(
                (await Planner.GetPickedListsAsync(admin, 10, DateOnly.FromDateTime(DateTime.Now), Ct)).SelectMany(l => Planner.GetRowItems(admin, l)),
                i => i.Item.Id.Equals(owned.Id));

            var fromFirst = await GetQueueAsync(client, Sync.FindFilm(1181)!.Id);
            var fromOwned = await GetQueueAsync(client, owned.Id);

            Assert.Equal(new[] { first, copy, last }, fromFirst);
            Assert.Equal(new[] { copy, last }, fromOwned);
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, Ct);
            await DeleteListsAsync(client, "ow-3");
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task LocalTrailers_OwnedFilm_Unchanged()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var owned = await SetOwnedTmdbAsync(1191);
        try
        {
            await PutListAsync(client, "ow-4", 1191);
            await SetTrailerAsync(client, 1191, "owned-1191");

            // The upstream endpoints report the owned film's own trailers only: the copy's trailer is the action's.
            var trailers = await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/LocalTrailers?userId={admin.Id:N}");
            var dto = await GetAsync<JsonObject>(client, $"Items/{owned.Id:N}?userId={admin.Id:N}");

            Assert.Empty(trailers);
            Assert.Equal(0, (int?)dto["LocalTrailerCount"] ?? 0);
            Assert.Single(await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions"));
        }
        finally
        {
            await DeleteListsAsync(client, "ow-4");
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task Picks_NonAdmin_Forbidden()
    {
        var client = await SetUpAsync();
        var name = "regular-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("Users/New", new { Name = name, Password = "regular-pw" }, JsonDefaults.Options, Ct))
        {
            create.EnsureSuccessStatusCode();
        }

        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, name, "regular-pw"));

        using var picks = await userClient.GetAsync("Discovery/Picks", Ct);
        using var films = await userClient.GetAsync("Discovery/Films", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, picks.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, films.StatusCode);
    }

    private async Task<Guid[]> GetQueueAsync(HttpClient client, Guid itemId)
    {
        var queue = await GetAsync<JsonObject>(client, $"Items/{itemId:N}/Actions/watch-trailer/Queue");
        return queue["ItemIds"]!.AsArray().Select(n => Guid.Parse((string)n!)).ToArray();
    }

    private async Task<Guid> SetTrailerAsync(HttpClient client, int tmdbId, string name)
    {
        var path = Path.Combine(_trailerRoot, name + "-trailer.mkv");
        await File.WriteAllBytesAsync(path, new byte[1024], Ct);
        using (var response = await client.PutAsJsonAsync($"Discovery/Films/{tmdbId}/Trailer", new { Path = path }, JsonDefaults.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var film = Sync.FindFilm(tmdbId)!;
        return Assert.Single(film.GetExtras([MediaBrowser.Model.Entities.ExtraType.Trailer])).Id;
    }

    // The seeded library film, given (or cleared of) a TMDB id so that a list can name it.
    private async Task<BaseItem> SetOwnedTmdbAsync(int? tmdbId)
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
        return film;
    }

    // The seeded library film, given (or cleared of) an IMDb id, with no TMDB id.
    private async Task<BaseItem> SetOwnedImdbAsync(string? imdbId)
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var film = LibraryManager.GetItemById(seed.RealFilm.Id)!;
        film.ProviderIds.Remove(MetadataProvider.Tmdb.ToString());
        if (imdbId is not null)
        {
            film.SetProviderId(MetadataProvider.Imdb, imdbId);
        }
        else
        {
            film.ProviderIds.Remove(MetadataProvider.Imdb.ToString());
        }

        await film.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
        return film;
    }

    // A trailer extra of the library film itself, like a local "-trailer" file next to it.
    private BaseItem AddOwnTrailer(BaseItem film)
    {
        var trailer = new Video
        {
            Name = film.Name + " Own Trailer",
            Path = Path.Combine(_trailerRoot, "own-" + Guid.NewGuid().ToString("N")[..8] + "-trailer.mkv"),
            OwnerId = film.Id,
            ExtraType = ExtraType.Trailer,
        };
        trailer.Id = LibraryManager.GetNewItemId(trailer.Path, typeof(Video));
        LibraryManager.CreateItem(trailer, null);
        return trailer;
    }

    private async Task SaveLayoutAsync(User user, int maxRows, bool enabled)
    {
        var errors = await Layouts.SaveUserLayoutAsync(
            user,
            [new HomeRowInstance(Guid.NewGuid(), HomeRowProviderIds.MovieDiscovery, null, new JsonObject { ["maxRows"] = maxRows }, enabled)],
            Ct);
        Assert.Empty(errors);
    }

    private async Task<User> CreateUserAsync(string prefix)
        => await UserManager.CreateUserAsync(prefix + "-" + Guid.NewGuid().ToString("N")[..8]);

    private async Task<User> GetAdminAsync(HttpClient client)
        => UserManager.GetUserById((await AuthHelper.GetUserDtoAsync(client)).Id)!;

    private async Task DeleteUsersAsync(params User[] users)
    {
        foreach (var user in users)
        {
            await Layouts.ResetUserLayoutAsync(user, Ct);
            await UserManager.DeleteUserAsync(user.Id);
        }
    }

    private void SetTrailerRoot(string? root)
        => _factory.Services.GetRequiredService<IConfigurationManager>()
            .SaveConfiguration("discovery", new DiscoveryOptions { TrailerRoot = root });

    private static async Task PutListAsync(HttpClient client, string listId, params int[] tmdbIds)
    {
        using var response = await client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new { Title = listId, Source = "tests", Films = tmdbIds.Select(t => new { TmdbId = t }).ToArray() },
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
