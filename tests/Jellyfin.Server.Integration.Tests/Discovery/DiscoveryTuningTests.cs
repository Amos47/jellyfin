using System;
using System.Collections.Generic;
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
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// List groups, the on-demand re-pick and the Movie discovery row's external trailers setting. Its own host: a
/// re-pick changes the day's picks for every user, which must not leak into the other discovery tests.
/// </summary>
public sealed class DiscoveryTuningTests : IClassFixture<JellyfinApplicationFactory>
{
    private const string YouTubeUrl = "https://www.youtube.com/watch?v=tuning";

    private readonly JellyfinApplicationFactory _factory;

    public DiscoveryTuningTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IDiscoveryStore Store => _factory.Services.GetRequiredService<IDiscoveryStore>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private IHomeRowConfigurationService Layouts => _factory.Services.GetRequiredService<IHomeRowConfigurationService>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    [Fact]
    public async Task Reshuffle_ChangesPicks_AndIsStableAfterwards()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("reshuffle");
        var listIds = Enumerable.Range(1, 8).Select(i => "rs-" + i).ToArray();
        try
        {
            for (var i = 0; i < listIds.Length; i++)
            {
                await PutListAsync(client, listIds[i], null, 1600 + i);
            }

            await SaveLayoutAsync(user, new JsonObject { ["maxRows"] = 2 });
            var date = new DateOnly(2032, 5, 5);
            var url = "Discovery/Picks?date=2032-05-05";

            // No salt yet: today's algorithm.
            var lists = await Store.GetListsAsync(Ct);
            var before = await GetPickedAsync(client, url);
            Assert.Equal(ExpectedFilms(user, date, lists, 2, null), before);

            var changed = false;
            string? salt = null;
            for (var attempt = 0; attempt < 10 && !changed; attempt++)
            {
                using var response = await client.PostAsJsonAsync("Discovery/Picks/Reshuffle", new { Date = "2032-05-05" }, JsonDefaults.Options, Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
                Assert.Equal("2032-05-05", (string?)body!["Date"]);
                salt = (string?)body["Salt"];
                Assert.False(string.IsNullOrEmpty(salt));
                Assert.Equal(salt, await Store.GetPickSaltAsync(date, Ct));

                var after = await GetPickedAsync(client, url);
                Assert.Equal(ExpectedFilms(user, date, lists, 2, salt), after);
                changed = !after.SequenceEqual(before);
            }

            Assert.True(changed, "ten re-picks never changed the picks");

            // Stable until the next re-pick: the same rows on every read.
            var first = await GetPickedAsync(client, url);
            var second = await GetPickedAsync(client, url);
            Assert.Equal(first, second);
            Assert.Equal(ExpectedFilms(user, date, lists, 2, salt), first);

            // Other days keep their own picks.
            var otherDay = await GetPickedAsync(client, "Discovery/Picks?date=2032-05-06");
            Assert.Equal(ExpectedFilms(user, date.AddDays(1), lists, 2, null), otherDay);
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, listIds);
        }
    }

    [Fact]
    public async Task Reshuffle_NoBody_Today_HomeRowsFollow()
    {
        var client = await SetUpAsync();
        var listIds = Enumerable.Range(1, 6).Select(i => "rt-" + i).ToArray();
        var parentId = Guid.NewGuid();
        try
        {
            for (var i = 0; i < listIds.Length; i++)
            {
                await PutListAsync(client, listIds[i], null, 1650 + i);
            }

            var admin = await GetAdminAsync(client);
            await SaveLayoutAsync(admin, new JsonObject { ["maxRows"] = 2 }, parentId);
            var today = DateOnly.FromDateTime(DateTime.Now);

            using var response = await client.PostAsync("Discovery/Picks/Reshuffle", null, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
            Assert.Equal(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), (string?)body!["Date"]);
            var salt = (string)body["Salt"]!;

            var expected = DiscoveryPicker.Pick(admin.Id, today, await Store.GetListsAsync(Ct), 2, salt).Select(l => l.Title).ToArray();
            using var config = await client.GetAsync("HomeRows/Configuration?expand=true", Ct);
            var rows = (await config.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct))!["Rows"]!.AsArray()
                .Where(r => (string?)r!["Provider"] == HomeRowProviderIds.MovieDiscovery)
                .Select(r => Guid.Parse((string)r!["Id"]!))
                .ToArray();
            var titles = new List<string?>();
            foreach (var rowId in rows)
            {
                using var items = await client.GetAsync($"HomeRows/{rowId:D}/Items", Ct);
                titles.Add((string?)(await items.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct))!["Title"]);
            }

            Assert.Equal(expected, titles);
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(await GetAdminAsync(client), Ct);
            await DeleteListsAsync(client, listIds);
        }
    }

    [Fact]
    public async Task Reshuffle_RequiresAdmin()
    {
        var client = await SetUpAsync();
        var name = "regular-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("Users/New", new { Name = name, Password = "regular-pw" }, JsonDefaults.Options, Ct))
        {
            create.EnsureSuccessStatusCode();
        }

        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, name, "regular-pw"));
        var date = new DateOnly(2032, 6, 6);

        using var response = await userClient.PostAsJsonAsync("Discovery/Picks/Reshuffle", new { Date = "2032-06-06" }, JsonDefaults.Options, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await Store.GetPickSaltAsync(date, Ct));
    }

    [Fact]
    public async Task PutList_Group_RoundTrips_BlankIsNull_TooLongRejected()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "grp-a", "  genre:horror  ", 1701);
            await PutListAsync(client, "grp-b", "   ", 1702);
            await PutListAsync(client, "grp-c", null, 1703);
            await PutListAsync(client, "grp-d", new string('g', 100), 1704);

            var lists = await GetAsync<JsonArray>(client, "Discovery/Lists");
            Assert.Equal("genre:horror", (string?)Find(lists, "grp-a")["Group"]);
            Assert.Null((string?)Find(lists, "grp-b")["Group"]);
            Assert.Null((string?)Find(lists, "grp-c")["Group"]);
            Assert.Equal(new string('g', 100), (string?)Find(lists, "grp-d")["Group"]);

            using var tooLong = await SendPutAsync(client, "grp-e", new string('g', 101), 1705);
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, "Discovery/Lists"), l => (string?)l!["Id"] == "grp-e");
        }
        finally
        {
            await DeleteListsAsync(client, "grp-a", "grp-b", "grp-c", "grp-d", "grp-e");
        }
    }

    [Fact]
    public async Task DiscoveryFilm_HasNoRemoteTrailersByDefault()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("no-external");
        var withRowOff = await CreateUserAsync("external-off");
        try
        {
            var film = await PutFilmWithRemoteTrailerAsync(client, "ext-off", 1801);
            await SaveLayoutAsync(withRowOff, new JsonObject { ["maxRows"] = 1, ["externalTrailers"] = false });

            foreach (var u in new[] { user, withRowOff })
            {
                Assert.Empty((await GetItemAsync(client, film.Id, u)).RemoteTrailers);
                Assert.Empty((await GetItemByIdsAsync(client, film.Id, u)).RemoteTrailers);
            }

            // Local trailers and the stored item are untouched.
            Assert.Single(LibraryManager.GetItemById(film.Id)!.RemoteTrailers);
        }
        finally
        {
            await DeleteUsersAsync(user, withRowOff);
            await DeleteListsAsync(client, "ext-off");
        }
    }

    [Fact]
    public async Task ExternalTrailersTrue_KeepsThem()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("external-on");
        try
        {
            var film = await PutFilmWithRemoteTrailerAsync(client, "ext-on", 1811);
            await SaveLayoutAsync(user, new JsonObject { ["maxRows"] = 1, ["externalTrailers"] = true });

            Assert.Equal(YouTubeUrl, Assert.Single((await GetItemAsync(client, film.Id, user)).RemoteTrailers).Url);
            Assert.Equal(YouTubeUrl, Assert.Single((await GetItemByIdsAsync(client, film.Id, user)).RemoteTrailers).Url);
        }
        finally
        {
            await DeleteUsersAsync(user);
            await DeleteListsAsync(client, "ext-on");
        }
    }

    [Fact]
    public async Task HomeRowItems_RemoteTrailersFollowTheExternalTrailersSetting()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        try
        {
            var film = await PutFilmWithRemoteTrailerAsync(client, "ext-row", 1821);
            var parentId = Guid.NewGuid();

            // Default: the setting is absent, so off.
            await SaveLayoutAsync(admin, new JsonObject { ["maxRows"] = 1 }, parentId);
            var rowId = Assert.Single((await GetAsync<HomeRowConfigurationDto>(client, "HomeRows/Configuration?expand=true")).Rows).Id;
            var hidden = Assert.Single((await GetRowItemsAsync(client, rowId)).Items, i => i.Id.Equals(film.Id));
            Assert.Empty(hidden.RemoteTrailers);

            await SaveLayoutAsync(admin, new JsonObject { ["maxRows"] = 1, ["externalTrailers"] = true }, parentId);
            rowId = Assert.Single((await GetAsync<HomeRowConfigurationDto>(client, "HomeRows/Configuration?expand=true")).Rows).Id;
            var shown = Assert.Single((await GetRowItemsAsync(client, rowId)).Items, i => i.Id.Equals(film.Id));
            Assert.Equal(YouTubeUrl, Assert.Single(shown.RemoteTrailers).Url);
        }
        finally
        {
            await DeleteLayoutAsync(admin);
            await DeleteListsAsync(client, "ext-row");
        }
    }

    [Fact]
    public async Task OwnedFilm_KeepsRemoteTrailers()
    {
        var client = await SetUpAsync();
        var user = await CreateUserAsync("owned-external");
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var owned = LibraryManager.GetItemById(seed.RealFilm.Id)!;
        var before = owned.RemoteTrailers;
        owned.RemoteTrailers = [new MediaUrl { Url = YouTubeUrl, Name = "Trailer" }];
        await owned.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
        try
        {
            Assert.Equal(YouTubeUrl, Assert.Single((await GetItemAsync(client, owned.Id, user)).RemoteTrailers).Url);
            Assert.Equal(YouTubeUrl, Assert.Single((await GetItemByIdsAsync(client, owned.Id, user)).RemoteTrailers).Url);
        }
        finally
        {
            owned.RemoteTrailers = before;
            await owned.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
            await DeleteUsersAsync(user);
        }
    }

    private static Task<HomeRowItemsDto> GetRowItemsAsync(HttpClient client, Guid rowId)
        => GetAsync<HomeRowItemsDto>(client, $"HomeRows/{rowId:D}/Items?fields=RemoteTrailers");

    private Task DeleteLayoutAsync(User user) => Layouts.ResetUserLayoutAsync(user, Ct);

    private static JsonNode Find(JsonArray lists, string id) => Assert.Single(lists, l => (string?)l!["Id"] == id)!;

    private static int[] ExpectedFilms(User user, DateOnly day, IEnumerable<DiscoveryList> lists, int maxRows, string? salt)
        => DiscoveryPicker.Pick(user.Id, day, lists, maxRows, salt).SelectMany(l => l.Films).Select(f => f.TmdbId).Order().ToArray();

    private static async Task<int[]> GetPickedAsync(HttpClient client, string url)
        => (await GetAsync<JsonObject>(client, url))["TmdbIds"]!.AsArray().Select(n => (int)n!).Order().ToArray();

    private async Task<BaseItem> PutFilmWithRemoteTrailerAsync(HttpClient client, string listId, int tmdbId)
    {
        await PutListAsync(client, listId, null, tmdbId);
        var film = Sync.FindFilm(tmdbId)!;
        film.RemoteTrailers = [new MediaUrl { Url = YouTubeUrl, Name = "Trailer" }];
        await film.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
        return film;
    }

    private static Task<BaseItemDto> GetItemAsync(HttpClient client, Guid itemId, User user)
        => GetAsync<BaseItemDto>(client, $"Items/{itemId:N}?userId={user.Id:N}");

    private static async Task<BaseItemDto> GetItemByIdsAsync(HttpClient client, Guid itemId, User user)
        => Assert.Single((await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?ids={itemId:N}&userId={user.Id:N}&fields=RemoteTrailers")).Items);

    private async Task SaveLayoutAsync(User user, JsonObject config, Guid? rowId = null)
    {
        var errors = await Layouts.SaveUserLayoutAsync(
            user,
            [new HomeRowInstance(rowId ?? Guid.NewGuid(), HomeRowProviderIds.MovieDiscovery, null, config, true)],
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

    private static Task<HttpResponseMessage> SendPutAsync(HttpClient client, string listId, string? group, params int[] tmdbIds)
        => client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new { Title = listId, Source = "tests", Group = group, Films = tmdbIds.Select(t => new { TmdbId = t }).ToArray() },
            JsonDefaults.Options,
            Ct);

    private static async Task PutListAsync(HttpClient client, string listId, string? group, params int[] tmdbIds)
    {
        using var response = await SendPutAsync(client, listId, group, tmdbIds);
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
