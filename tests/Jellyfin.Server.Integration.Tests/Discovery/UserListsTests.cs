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
/// Per-profile lists (Saved): the save toggle on films and their trailers, the user list endpoints, the user-list home
/// row, user list pages and the film sync keeping saved films. Its own host: it gives the seeded library film a TMDB id.
/// </summary>
public sealed class UserListsTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public UserListsTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-user-lists-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    private IHomeRowConfigurationService Layouts => _factory.Services.GetRequiredService<IHomeRowConfigurationService>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_trailerRoot, true);
    }

    [Fact]
    public async Task Save_TogglesPerUser()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await PutListAsync(client, "ul-save", 4001);
            var filmId = Sync.FindFilm(4001)!.Id;

            var before = SaveAction(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions"));
            Assert.Equal("toggle", (string?)before["Kind"]);
            Assert.Equal("off", (string?)before["State"]);
            Assert.Equal("Save", (string?)before["Label"]);
            Assert.Equal("Saved", (string?)before["Labels"]!["on"]);
            Assert.Equal("Save", (string?)before["Labels"]!["off"]);

            // The admin saves it: on for them, still off for the other profile.
            var saved = await PostAsync<JsonObject>(client, $"Items/{filmId:N}/Actions/save");
            Assert.Equal("on", (string?)saved["State"]);
            Assert.Equal("Saved", (string?)saved["Label"]);
            Assert.Equal("on", (string?)SaveAction(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions"))["State"]);
            Assert.Equal("off", (string?)SaveAction(await GetAsync<JsonArray>(userClient, $"Items/{filmId:N}/Actions"))["State"]);

            var adminSaved = await GetSavedAsync(client);
            var userSaved = await GetSavedAsync(userClient);
            Assert.Equal("Saved", (string?)adminSaved["Name"]);
            Assert.True((bool)adminSaved["IsDefault"]!);
            Assert.NotEqual((string?)adminSaved["Id"], (string?)userSaved["Id"]);
            Assert.Equal(new[] { 4001 }, FilmTmdbIds(adminSaved));
            Assert.Empty(FilmTmdbIds(userSaved));

            // Toggled again: off, and gone from Saved.
            var unsaved = await PostAsync<JsonObject>(client, $"Items/{filmId:N}/Actions/save");
            Assert.Equal("off", (string?)unsaved["State"]);
            Assert.Empty(FilmTmdbIds(await GetSavedAsync(client)));
        }
        finally
        {
            await UserManager.DeleteUserAsync(user.Id);
            await DeleteListsAsync(client, "ul-save");
        }
    }

    [Fact]
    public async Task Save_ShownOnlyOnDiscoveryOrListedFilms()
    {
        var client = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var owned = await SetOwnedTmdbAsync(4011);
        try
        {
            // A library film that isn't in any of the user's lists has no Save.
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions"), a => (string?)a!["Id"] == "save");

            // Once it is in one (saved while it was a discovery film, kept by TMDB id), Save shows, on.
            var savedId = (string)(await GetSavedAsync(client))["Id"]!;
            await PostAsync<JsonObject>(client, $"Discovery/UserLists/{savedId}/Films/4011");
            Assert.Equal("on", (string?)SaveAction(await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions"))["State"]);

            // Toggled off it answers off; then it is no longer shown.
            var off = await PostAsync<JsonObject>(client, $"Items/{owned.Id:N}/Actions/save");
            Assert.Equal("off", (string?)off["State"]);
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions"), a => (string?)a!["Id"] == "save");

            // Not a film at all: the library itself has no Save.
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{seed.Library.Id:N}/Actions"), a => (string?)a!["Id"] == "save");
        }
        finally
        {
            await SetOwnedTmdbAsync(null);
        }
    }

    [Fact]
    public async Task TrailerActions_ResolveToFilm()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "ul-trailer", 4021);
            var trailerId = await SetTrailerAsync(client, 4021, "ul-4021");

            // The trailer's actions are its film's, without Watch trailer.
            var actions = await GetAsync<JsonArray>(client, $"Items/{trailerId:N}/Actions");
            Assert.DoesNotContain(actions, a => (string?)a!["Id"] == "watch-trailer");
            Assert.Equal("off", (string?)SaveAction(actions)["State"]);

            // The film itself still has Watch trailer.
            Assert.Contains(await GetAsync<JsonArray>(client, $"Items/{Sync.FindFilm(4021)!.Id:N}/Actions"), a => (string?)a!["Id"] == "watch-trailer");

            // A POST on the trailer saves the film.
            var saved = await PostAsync<JsonObject>(client, $"Items/{trailerId:N}/Actions/save");
            Assert.Equal("on", (string?)saved["State"]);
            Assert.Equal(new[] { 4021 }, FilmTmdbIds(await GetSavedAsync(client)));
            Assert.Equal("on", (string?)SaveAction(await GetAsync<JsonArray>(client, $"Items/{Sync.FindFilm(4021)!.Id:N}/Actions"))["State"]);

            // A play-queue action is played, never posted: 400 on the film and on its trailer alike.
            using var post = await client.PostAsync($"Items/{Sync.FindFilm(4021)!.Id:N}/Actions/watch-trailer", null, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
            using var postOnTrailer = await client.PostAsync($"Items/{trailerId:N}/Actions/watch-trailer", null, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, postOnTrailer.StatusCode);
            using var unknown = await client.PostAsync($"Items/{Sync.FindFilm(4021)!.Id:N}/Actions/no-such-action", null, Ct);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await ClearSavedAsync(client);
            await DeleteListsAsync(client, "ul-trailer");
        }
    }

    [Fact]
    public async Task UserListRow_ShowsSavedNewestFirst_EvenWithoutTrailer()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        try
        {
            // 4032 never gets a trailer: a saved film shows anyway.
            await PutListAsync(client, "ul-row", 4031, 4032, 4033);
            await SetTrailerAsync(client, 4031, "ul-4031");
            await SetTrailerAsync(client, 4033, "ul-4033");
            var savedId = (string)(await GetSavedAsync(client))["Id"]!;
            foreach (var tmdbId in new[] { 4031, 4032, 4033 })
            {
                await PostAsync<JsonObject>(client, $"Discovery/UserLists/{savedId}/Films/{tmdbId}");
            }

            var rowId = Guid.NewGuid();
            Assert.Empty(await Layouts.SaveUserLayoutAsync(admin, [new HomeRowInstance(rowId, HomeRowProviderIds.UserList, null, new JsonObject(), true)], Ct));

            var row = await GetAsync<JsonObject>(client, $"HomeRows/{rowId:D}/Items");

            Assert.Equal("Saved", (string?)row["Title"]);
            Assert.Equal(
                new[] { Sync.FindFilm(4033)!.Id, Sync.FindFilm(4032)!.Id, Sync.FindFilm(4031)!.Id },
                row["Items"]!.AsArray().Select(n => Guid.Parse((string)n!["Id"]!)).ToArray());
            var pageListId = (string?)row["Presentation"]!["PageListId"];
            Assert.Equal("user:" + Guid.Parse(savedId).ToString("N"), pageListId);

            // The page of that list shows the same films, the one without a trailer too.
            var page = await GetAsync<JsonObject>(client, $"Discovery/Lists/{pageListId}/Page");
            Assert.Equal("Saved", (string?)page["Title"]);
            Assert.Equal(3, page["Items"]!.AsArray().Count);
            var queue = await GetAsync<JsonObject>(client, $"Discovery/Lists/{pageListId}/Actions/watch-trailers/Queue");
            Assert.Equal(2, queue["ItemIds"]!.AsArray().Count);

            // The row's list setting offers the user's lists, Saved by default.
            var providers = await GetAsync<JsonObject>(client, "HomeRows/Providers");
            var provider = providers["Providers"]!.AsArray().Single(p => (string?)p!["Id"] == HomeRowProviderIds.UserList)!;
            Assert.Equal("default", (string?)provider["Defaults"]!["list"]);
            Assert.Contains(provider["Schema"]!["properties"]!["list"]!["enum"]!.AsArray(), v => (string?)v == "default");
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, Ct);
            await ClearSavedAsync(client);
            await DeleteListsAsync(client, "ul-row");
        }
    }

    [Fact]
    public async Task Sync_KeepsSavedFilmWhenListRotatesOut()
    {
        var client = await SetUpAsync();
        var admin = await GetAdminAsync(client);
        try
        {
            await PutListAsync(client, "ul-sync", 4041, 4042);
            var trailerId = await SetTrailerAsync(client, 4041, "ul-4041");
            var saved = await GetSavedAsync(client);
            var savedId = (string)saved["Id"]!;
            await PostAsync<JsonObject>(client, $"Discovery/UserLists/{savedId}/Films/4041");
            var rowId = Guid.NewGuid();
            Assert.Empty(await Layouts.SaveUserLayoutAsync(admin, [new HomeRowInstance(rowId, HomeRowProviderIds.UserList, null, new JsonObject(), true)], Ct));

            // The list rotates out: the saved film stays with its trailer, the other goes.
            await DeleteListsAsync(client, "ul-sync");
            var film = Sync.FindFilm(4041);
            Assert.NotNull(film);
            Assert.Null(Sync.FindFilm(4042));
            Assert.Equal(new[] { trailerId }, film.GetExtras([ExtraType.Trailer]).Select(t => t.Id).ToArray());
            Assert.NotNull(LibraryManager.GetItemById(trailerId));
            Assert.Equal(new[] { 4041 }, FilmTmdbIds(await GetSavedAsync(client)));

            // It still shows in the Saved row and on its page.
            var row = await GetAsync<JsonObject>(client, $"HomeRows/{rowId:D}/Items");
            Assert.Equal(new[] { film.Id }, row["Items"]!.AsArray().Select(n => Guid.Parse((string)n!["Id"]!)).ToArray());
            var page = await GetAsync<JsonObject>(client, $"Discovery/Lists/{(string)saved["PageListId"]!}/Page");
            Assert.Equal(new[] { film.Id }, page["Items"]!.AsArray().Select(n => Guid.Parse((string)n!["Id"]!)).ToArray());

            // Removed from Saved, the next sync deletes it.
            await DeleteAsync(client, $"Discovery/UserLists/{savedId}/Films/4041");
            await Sync.SyncAsync(Ct);
            Assert.Null(Sync.FindFilm(4041));
        }
        finally
        {
            await Layouts.ResetUserLayoutAsync(admin, Ct);
            await ClearSavedAsync(client);
            await DeleteListsAsync(client, "ul-sync");
        }
    }

    [Fact]
    public async Task SavedRow_InStandardDefault_EmptyUntilSomethingIsSaved()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await PutListAsync(client, "ul-default", 4071);

            // A fresh profile on the standard default has the Saved row; with nothing saved it has no items, which
            // every client hides like any empty row.
            var layout = await GetAsync<JsonObject>(userClient, "HomeRows/Configuration");
            Assert.Equal("StandardDefault", (string?)layout["Source"]);
            var savedRow = layout["Rows"]!.AsArray().Single(r => (string?)r!["Provider"] == HomeRowProviderIds.UserList)!;
            var rowId = (string)savedRow["Id"]!;
            var empty = await GetAsync<JsonObject>(userClient, $"HomeRows/{rowId}/Items");
            Assert.Empty(empty["Items"]!.AsArray());
            Assert.Equal(0, (int?)empty["TotalRecordCount"]);
            Assert.Null(empty["Error"]);

            await PostAsync<JsonObject>(userClient, $"Items/{Sync.FindFilm(4071)!.Id:N}/Actions/save");
            var one = await GetAsync<JsonObject>(userClient, $"HomeRows/{rowId}/Items");
            Assert.Equal(new[] { Sync.FindFilm(4071)!.Id }, one["Items"]!.AsArray().Select(n => Guid.Parse((string)n!["Id"]!)).ToArray());
        }
        finally
        {
            await UserManager.DeleteUserAsync(user.Id);
            await DeleteListsAsync(client, "ul-default");
        }
    }

    [Fact]
    public async Task DeletedUser_ListsGo_FilmNoLongerProtected()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        await PutListAsync(client, "ul-deleted", 4081);
        await PostAsync<JsonObject>(userClient, $"Items/{Sync.FindFilm(4081)!.Id:N}/Actions/save");
        var lists = _factory.Services.GetRequiredService<IUserListStore>();
        Assert.Contains(4081, await lists.GetListedTmdbIdsAsync(Ct));

        await UserManager.DeleteUserAsync(user.Id);

        Assert.DoesNotContain(4081, await lists.GetListedTmdbIdsAsync(Ct));
        await DeleteListsAsync(client, "ul-deleted");
        Assert.Null(Sync.FindFilm(4081));
    }

    [Fact]
    public async Task NonAdminCanSave()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await PutListAsync(client, "ul-nonadmin", 4051, 4052);
            var filmId = Sync.FindFilm(4051)!.Id;

            var lists = await GetAsync<JsonArray>(userClient, "Discovery/UserLists");
            var saved = Assert.Single(lists)!.AsObject();
            var savedId = (string)saved["Id"]!;

            Assert.Equal("on", (string?)(await PostAsync<JsonObject>(userClient, $"Items/{filmId:N}/Actions/save"))["State"]);
            var afterAdd = await PostAsync<JsonObject>(userClient, $"Discovery/UserLists/{savedId}/Films/4052");
            Assert.Equal(new[] { 4052, 4051 }, FilmTmdbIds(afterAdd));
            Assert.Equal(new[] { 4052, 4051 }, FilmTmdbIds(await GetAsync<JsonObject>(userClient, $"Discovery/UserLists/{savedId}")));

            var afterRemove = await DeleteAsync(userClient, $"Discovery/UserLists/{savedId}/Films/4052");
            Assert.Equal(new[] { 4051 }, FilmTmdbIds(afterRemove));

            // A film the server doesn't know can't be saved.
            using var unknown = await userClient.PostAsync($"Discovery/UserLists/{savedId}/Films/4999", null, Ct);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await UserManager.DeleteUserAsync(user.Id);
            await DeleteListsAsync(client, "ul-nonadmin");
        }
    }

    [Fact]
    public async Task UserListPage_OnlyOwner()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await PutListAsync(client, "ul-owner", 4061);
            await SetTrailerAsync(client, 4061, "ul-4061");
            var saved = await GetSavedAsync(client);
            var savedId = (string)saved["Id"]!;
            var pageListId = (string)saved["PageListId"]!;
            await PostAsync<JsonObject>(client, $"Discovery/UserLists/{savedId}/Films/4061");

            Assert.Single((await GetAsync<JsonObject>(client, $"Discovery/Lists/{pageListId}/Page"))["Items"]!.AsArray());
            Assert.Contains(await GetAsync<JsonArray>(client, $"Discovery/Lists/{pageListId}/Actions"), a => (string?)a!["Id"] == "watch-trailers");
            var filmId = Sync.FindFilm(4061)!.Id;
            var queue = await GetAsync<JsonObject>(client, $"Items/{filmId:N}/Actions/watch-trailer/Queue?listId={pageListId}");
            Assert.Single(queue["ItemIds"]!.AsArray());

            // Another profile gets 404 for every route of that list.
            foreach (var url in new[]
            {
                $"Discovery/Lists/{pageListId}/Page",
                $"Discovery/Lists/{pageListId}/Actions",
                $"Discovery/Lists/{pageListId}/Actions/watch-trailers/Queue",
                $"Discovery/UserLists/{savedId}",
                "Discovery/Lists/user:not-a-guid/Page",
            })
            {
                using var response = await userClient.GetAsync(url, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"GET {url}: {response.StatusCode}");
            }

            using (var post = await userClient.PostAsync($"Discovery/UserLists/{savedId}/Films/4061", null, Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            }

            using (var delete = await userClient.DeleteAsync($"Discovery/UserLists/{savedId}/Films/4061", Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
            }

            Assert.Equal(new[] { 4061 }, FilmTmdbIds(await GetSavedAsync(client)));
        }
        finally
        {
            await UserManager.DeleteUserAsync(user.Id);
            await ClearSavedAsync(client);
            await DeleteListsAsync(client, "ul-owner");
        }
    }

    private static JsonObject SaveAction(JsonArray actions)
        => actions.Single(a => (string?)a!["Id"] == "save")!.AsObject();

    private static int[] FilmTmdbIds(JsonObject list)
        => list["Films"]!.AsArray().Select(f => (int)f!["TmdbId"]!).ToArray();

    private static async Task<JsonObject> GetSavedAsync(HttpClient client)
        => (await GetAsync<JsonArray>(client, "Discovery/UserLists")).Single(l => (bool)l!["IsDefault"]!)!.AsObject();

    private static async Task ClearSavedAsync(HttpClient client)
    {
        var saved = await GetSavedAsync(client);
        foreach (var tmdbId in FilmTmdbIds(saved))
        {
            await DeleteAsync(client, $"Discovery/UserLists/{(string)saved["Id"]!}/Films/{tmdbId}");
        }
    }

    private async Task<Guid> SetTrailerAsync(HttpClient client, int tmdbId, string name)
    {
        var path = Path.Combine(_trailerRoot, name + "-trailer.mkv");
        await File.WriteAllBytesAsync(path, new byte[1024], Ct);
        using (var response = await client.PutAsJsonAsync($"Discovery/Films/{tmdbId}/Trailer", new { Path = path }, JsonDefaults.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        return Assert.Single(Sync.FindFilm(tmdbId)!.GetExtras([ExtraType.Trailer])).Id;
    }

    // The seeded library film, given (or cleared of) a TMDB id.
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

    private async Task<User> GetAdminAsync(HttpClient client)
        => UserManager.GetUserById((await AuthHelper.GetUserDtoAsync(client)).Id)!;

    private async Task<(User User, HttpClient Client)> CreateNonAdminAsync()
    {
        var client = await SetUpAsync();
        var name = "saved-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("Users/New", new { Name = name, Password = "saved-pw" }, JsonDefaults.Options, Ct))
        {
            create.EnsureSuccessStatusCode();
        }

        var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, name, "saved-pw"));
        return (UserManager.GetUserByName(name)!, userClient);
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

    private static async Task<T> PostAsync<T>(HttpClient client, string url)
    {
        using var response = await client.PostAsync(url, null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {url}: {response.StatusCode}");
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, Ct);
        Assert.NotNull(value);
        return value;
    }

    private static async Task<JsonObject> DeleteAsync(HttpClient client, string url)
    {
        using var response = await client.DeleteAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"DELETE {url}: {response.StatusCode}");
        var value = await response.Content.ReadFromJsonAsync<JsonObject>(JsonDefaults.Options, Ct);
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
