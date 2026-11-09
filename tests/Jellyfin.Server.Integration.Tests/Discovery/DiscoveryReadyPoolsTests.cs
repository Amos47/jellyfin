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
using Jellyfin.Api.Models.HomeRowDtos;
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
/// Ready pools: with the Movie discovery row's <c>requireTrailer</c> on (the default), rows show only films the user
/// can play, only lists with enough of them are picked, and the day's picks are saved. Its own host: re-picks and
/// saved picks are server wide.
/// </summary>
public sealed class DiscoveryReadyPoolsTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public DiscoveryReadyPoolsTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-discovery-ready-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IDiscoveryStore Store => _factory.Services.GetRequiredService<IDiscoveryStore>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private IHomeRowConfigurationService Layouts => _factory.Services.GetRequiredService<IHomeRowConfigurationService>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_trailerRoot, true);
    }

    [Fact]
    public async Task Picks_SavedForTheDay_ListBecomingReadyDoesNotChangeThem()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("ready-saved");
        try
        {
            await PutListAsync(client, "rp-sv-a", 3001);
            await PutListAsync(client, "rp-sv-b", 3002);
            await SetTrailerAsync(client, 3001);

            // One row asked for and one ready: the pick is full, so it is saved.
            await SaveLayoutAsync(user, new JsonObject { ["maxRows"] = 1, ["minReadyFilms"] = 1 });
            const string Url = "Discovery/Picks?date=2033-01-10";

            Assert.Equal(new[] { 3001 }, await GetPickedAsync(client, Url));

            // The other list becomes ready later that day: the saved picks hold.
            await SetTrailerAsync(client, 3002);
            Assert.Equal(new[] { 3001 }, await GetPickedAsync(client, Url));
            Assert.Equal(new[] { "rp-sv-a" }, await Store.GetPickSnapshotAsync(user.Id, new DateOnly(2033, 1, 10), Ct));

            // The list losing its trailer mid-day stays picked too.
            await RemoveTrailerAsync(client, 3001);
            Assert.Equal(new[] { 3001 }, await GetPickedAsync(client, Url));
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, "rp-sv-a", "rp-sv-b");
        }
    }

    [Fact]
    public async Task Reshuffle_ClearsSavedPicks()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("ready-reshuffle");
        try
        {
            await PutListAsync(client, "rp-rs-a", 3011);
            await PutListAsync(client, "rp-rs-b", 3012);
            await SetTrailerAsync(client, 3011);
            await SaveLayoutAsync(user, new JsonObject { ["maxRows"] = 1, ["minReadyFilms"] = 1 });
            const string Url = "Discovery/Picks?date=2033-02-10";
            Assert.Equal(new[] { 3011 }, await GetPickedAsync(client, Url));
            await SetTrailerAsync(client, 3012);
            await RemoveTrailerAsync(client, 3011);
            Assert.Equal(new[] { 3011 }, await GetPickedAsync(client, Url));

            using (var response = await client.PostAsJsonAsync("Discovery/Picks/Reshuffle", new { Date = "2033-02-10" }, JsonDefaults.Options, Ct))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Null(await Store.GetPickSnapshotAsync(user.Id, new DateOnly(2033, 2, 10), Ct));
            Assert.Equal(new[] { 3012 }, await GetPickedAsync(client, Url));
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, "rp-rs-a", "rp-rs-b");
        }
    }

    [Fact]
    public async Task Picks_NextDayRecomputes()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("ready-next-day");
        try
        {
            await PutListAsync(client, "rp-nd-a", 3021);
            await PutListAsync(client, "rp-nd-b", 3022);
            await SetTrailerAsync(client, 3021);
            await SaveLayoutAsync(user, new JsonObject { ["maxRows"] = 1, ["minReadyFilms"] = 1 });
            Assert.Equal(new[] { 3021 }, await GetPickedAsync(client, "Discovery/Picks?date=2033-03-10"));

            await SetTrailerAsync(client, 3022);
            await RemoveTrailerAsync(client, 3021);

            Assert.Equal(new[] { 3021 }, await GetPickedAsync(client, "Discovery/Picks?date=2033-03-10"));
            Assert.Equal(new[] { 3022 }, await GetPickedAsync(client, "Discovery/Picks?date=2033-03-11"));
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, "rp-nd-a", "rp-nd-b");
        }
    }

    [Fact]
    public async Task Picks_SnapshotDropsDeletedLists()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("ready-deleted");
        try
        {
            await PutListAsync(client, "rp-dl-a", 3031);
            await PutListAsync(client, "rp-dl-b", 3032);
            await SetTrailerAsync(client, 3031);
            await SetTrailerAsync(client, 3032);
            await SaveLayoutAsync(user, new JsonObject { ["maxRows"] = 2, ["minReadyFilms"] = 1 });
            const string Url = "Discovery/Picks?date=2033-04-10";
            Assert.Equal(new[] { 3031, 3032 }, await GetPickedAsync(client, Url));
            Assert.Equal(2, (await Store.GetPickSnapshotAsync(user.Id, new DateOnly(2033, 4, 10), Ct))!.Count);

            await DeleteListsAsync(client, "rp-dl-b");

            Assert.Equal(new[] { 3031 }, await GetPickedAsync(client, Url));
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, "rp-dl-a", "rp-dl-b");
        }
    }

    [Fact]
    public async Task HomeRows_NothingReady_NoDiscoveryRows_ThenOnlyPlayableFilms()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        var owned = await SetOwnedTmdbAsync(3043);
        try
        {
            // Not ready yet: two playable films (one owned) where three are needed.
            await PutListAsync(client, "rp-hr-a", 3041, 3042, 3043, 3044);
            await SetTrailerAsync(client, 3041);
            await SaveLayoutAsync(admin, new JsonObject { ["maxRows"] = 3, ["minReadyFilms"] = 3 });

            Assert.Empty(await GetDiscoveryRowIdsAsync(client));

            // Ready now: a pick short of maxRows is not saved, so the list is picked on the next read.
            await SetTrailerAsync(client, 3044);
            var rowId = Assert.Single(await GetDiscoveryRowIdsAsync(client));
            var row = await GetAsync<HomeRowItemsDto>(client, $"HomeRows/{rowId:D}/Items");

            Assert.Equal("rp-hr-a", row.Title);
            Assert.Equal(new[] { Sync.FindFilm(3041)!.Id, owned.Id, Sync.FindFilm(3044)!.Id }, row.Items.Select(i => i.Id));
            Assert.Equal(3, row.TotalRecordCount);
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, CancellationToken.None);
            await DeleteListsAsync(client, "rp-hr-a");
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task HomeRows_RequireTrailerOff_ShowsEveryVisibleFilm()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        try
        {
            await PutListAsync(client, "rp-off-a", 3051, 3052);
            await SaveLayoutAsync(admin, new JsonObject { ["maxRows"] = 3, ["requireTrailer"] = false });

            var rowId = Assert.Single(await GetDiscoveryRowIdsAsync(client));
            var row = await GetAsync<HomeRowItemsDto>(client, $"HomeRows/{rowId:D}/Items");

            Assert.Equal(new[] { Sync.FindFilm(3051)!.Id, Sync.FindFilm(3052)!.Id }, row.Items.Select(i => i.Id));
            Assert.Null(await Store.GetPickSnapshotAsync(admin.Id, DateOnly.FromDateTime(DateTime.Now), Ct));
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, CancellationToken.None);
            await DeleteListsAsync(client, "rp-off-a");
        }
    }

    [Fact]
    public async Task Lists_ReportReadyFilmsAndReady()
    {
        var client = await SetUpAsync();
        var owned = await SetOwnedTmdbAsync(3065);
        try
        {
            // Five films with trailers and one without: ready.
            await PutListAsync(client, "rp-ls-a", 3061, 3062, 3063, 3064, 3066, 3067);
            foreach (var tmdbId in new[] { 3061, 3062, 3063, 3064, 3066 })
            {
                await SetTrailerAsync(client, tmdbId);
            }

            // Three with trailers, one owned, one without: four, not ready.
            await PutListAsync(client, "rp-ls-b", 3061, 3062, 3063, 3065, 3068);
            await PutListAsync(client, "rp-ls-c", 3068);

            var lists = await GetAsync<JsonArray>(client, "Discovery/Lists");

            var a = Find(lists, "rp-ls-a");
            Assert.Equal(5, (int)a["ReadyFilms"]!);
            Assert.True((bool)a["Ready"]!);
            var b = Find(lists, "rp-ls-b");
            Assert.Equal(4, (int)b["ReadyFilms"]!);
            Assert.False((bool)b["Ready"]!);
            var c = Find(lists, "rp-ls-c");
            Assert.Equal(0, (int)c["ReadyFilms"]!);
            Assert.False((bool)c["Ready"]!);
            Assert.NotNull(owned);
        }
        finally
        {
            await DeleteListsAsync(client, "rp-ls-a", "rp-ls-b", "rp-ls-c");
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task Lists_Ready_UsesLargestMinReadyFilms()
    {
        var client = await SetUpAsync();
        var lenient = await CreateUserAsync("ready-threshold-3");
        var strict = await CreateUserAsync("ready-threshold-4");
        try
        {
            await PutListAsync(client, "rp-th-four", 3071, 3072, 3073, 3074);
            await PutListAsync(client, "rp-th-three", 3071, 3072, 3073);
            foreach (var tmdbId in new[] { 3071, 3072, 3073, 3074 })
            {
                await SetTrailerAsync(client, tmdbId);
            }

            // The only Movie discovery row needs three: both lists are ready.
            await SaveLayoutAsync(lenient, new JsonObject { ["minReadyFilms"] = 3 });
            var lists = await GetAsync<JsonArray>(client, "Discovery/Lists");
            Assert.True((bool)Find(lists, "rp-th-four")["Ready"]!);
            Assert.True((bool)Find(lists, "rp-th-three")["Ready"]!);

            // Another user's row needs four: the largest is the bar.
            await SaveLayoutAsync(strict, new JsonObject { ["minReadyFilms"] = 4 });
            lists = await GetAsync<JsonArray>(client, "Discovery/Lists");
            Assert.True((bool)Find(lists, "rp-th-four")["Ready"]!);
            Assert.False((bool)Find(lists, "rp-th-three")["Ready"]!);
            Assert.Equal(3, (int)Find(lists, "rp-th-three")["ReadyFilms"]!);
        }
        finally
        {
            await DeleteUsersAsync(lenient, strict);
            await DeleteListsAsync(client, "rp-th-four", "rp-th-three");
        }
    }

    private static JsonNode Find(JsonArray lists, string id) => Assert.Single(lists, l => (string?)l!["Id"] == id)!;

    private static async Task<int[]> GetPickedAsync(HttpClient client, string url)
        => (await GetAsync<JsonObject>(client, url))["TmdbIds"]!.AsArray().Select(n => (int)n!).Order().ToArray();

    private static async Task<Guid[]> GetDiscoveryRowIdsAsync(HttpClient client)
    {
        var layout = await GetAsync<HomeRowConfigurationDto>(client, "HomeRows/Configuration?expand=true");
        return layout.Rows.Where(r => r.Provider == HomeRowProviderIds.MovieDiscovery).Select(r => r.Id).ToArray();
    }

    private async Task SetTrailerAsync(HttpClient client, int tmdbId)
    {
        var path = Path.Combine(_trailerRoot, "ready-" + tmdbId.ToString(CultureInfo.InvariantCulture) + "-trailer.mkv");
        await File.WriteAllBytesAsync(path, new byte[1024], Ct);
        using var response = await client.PutAsJsonAsync($"Discovery/Films/{tmdbId}/Trailer", new { Path = path }, JsonDefaults.Options, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task RemoveTrailerAsync(HttpClient client, int tmdbId)
    {
        using var response = await client.DeleteAsync($"Discovery/Films/{tmdbId}/Trailer", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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

    private async Task SaveLayoutAsync(User user, JsonObject config)
    {
        var errors = await Layouts.SaveUserLayoutAsync(
            user,
            [new HomeRowInstance(Guid.NewGuid(), HomeRowProviderIds.MovieDiscovery, null, config, true)],
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
            await Layouts.ResetUserLayoutAsync(user, CancellationToken.None);
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
            using var response = await client.DeleteAsync("Discovery/Lists/" + listId, CancellationToken.None);
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

    private async Task<HttpClient> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        return client;
    }
}
