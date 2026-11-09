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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// Download requests: the download action on discovery films (kind request), the admin routes the discovery app uses
/// (requests, the downloads setting, protected films) and the film sync keeping requested films. Its own host: it gives
/// the seeded library film a TMDB id to stand for a finished download.
/// </summary>
public sealed class DownloadRequestsTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public DownloadRequestsTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-downloads-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private IUserManager UserManager => _factory.Services.GetRequiredService<IUserManager>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_trailerRoot, true);
    }

    [Fact]
    public async Task Download_HiddenWhenDisabled()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "dl-hidden", 5001);
            var filmId = Sync.FindFilm(5001)!.Id;

            // Off until the app turns downloads on.
            await SetDownloadsAsync(client, false);
            Assert.Null(FindDownload(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions")));
            using (var post = await client.PostAsync($"Items/{filmId:N}/Actions/download", null, Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            }

            await SetDownloadsAsync(client, true);
            var action = FindDownload(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions"));
            Assert.NotNull(action);
            Assert.Equal("request", (string?)action["Kind"]);
            Assert.Equal("available", (string?)action["State"]);
            Assert.Equal("Download", (string?)action["Label"]);
            Assert.True((bool)action["Enabled"]!);
            Assert.Equal("Download", (string?)action["Labels"]!["available"]);
            Assert.Equal("Requested", (string?)action["Labels"]!["requested"]);
            Assert.Equal("Downloading", (string?)action["Labels"]!["downloading"]);

            // Off again: hidden again.
            await SetDownloadsAsync(client, false);
            Assert.Null(FindDownload(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions")));

            // Never on a library film.
            await SetDownloadsAsync(client, true);
            var seed = await DiscoverySeed.EnsureAsync(_factory);
            Assert.Null(FindDownload(await GetAsync<JsonArray>(client, $"Items/{seed.RealFilm.Id:N}/Actions")));
        }
        finally
        {
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "dl-hidden");
        }
    }

    [Fact]
    public async Task Download_RequestSavesForRequesterOnly()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await SetDownloadsAsync(client, true);
            await PutListAsync(client, "dl-saves", 5011);
            var filmId = Sync.FindFilm(5011)!.Id;

            // A non-admin requests it: requested, disabled, and in their Saved only.
            var requested = await PostAsync<JsonObject>(userClient, $"Items/{filmId:N}/Actions/download");
            Assert.Equal("requested", (string?)requested["State"]);
            Assert.Equal("Requested", (string?)requested["Label"]);
            Assert.False((bool)requested["Enabled"]!);
            Assert.Equal(new[] { 5011 }, FilmTmdbIds(await GetSavedAsync(userClient)));
            Assert.Empty(FilmTmdbIds(await GetSavedAsync(client)));

            // The request is per film: the admin sees it requested too.
            var adminView = FindDownload(await GetAsync<JsonArray>(client, $"Items/{filmId:N}/Actions"));
            Assert.Equal("requested", (string?)adminView!["State"]);

            var request = await GetRequestAsync(client, "requested", 5011);
            Assert.NotNull(request);
            Assert.Equal(new[] { user.Id }, RequestUserIds(request));
            Assert.Equal("requested", (string?)request["State"]);
        }
        finally
        {
            await SetDownloadsAsync(client, false);
            await UserManager.DeleteUserAsync(user.Id);
            await DeleteListsAsync(client, "dl-saves");
        }
    }

    [Fact]
    public async Task Download_StatesFlow_AvailableRequestedDownloadingDone()
    {
        var client = await SetUpAsync();
        try
        {
            await SetDownloadsAsync(client, true);
            await PutListAsync(client, "dl-flow", 5021);
            var filmId = Sync.FindFilm(5021)!.Id;
            var url = $"Items/{filmId:N}/Actions";

            Assert.Equal("available", (string?)FindDownload(await GetAsync<JsonArray>(client, url))!["State"]);

            Assert.Equal("requested", (string?)(await PostAsync<JsonObject>(client, url + "/download"))["State"]);
            Assert.Equal("requested", (string?)FindDownload(await GetAsync<JsonArray>(client, url))!["State"]);
            Assert.Contains(5021, FilmTmdbIds(await GetSavedAsync(client)));

            // The app reports it downloading.
            await PutRequestAsync(client, 5021, "downloading", HttpStatusCode.NoContent);
            var downloading = FindDownload(await GetAsync<JsonArray>(client, url))!;
            Assert.Equal("downloading", (string?)downloading["State"]);
            Assert.Equal("Downloading", (string?)downloading["Label"]);
            Assert.False((bool)downloading["Enabled"]!);
            Assert.NotNull(await GetRequestAsync(client, "downloading", 5021));
            Assert.Null(await GetRequestAsync(client, "requested", 5021));

            // Jellyfin has the file: the request is done and Download is gone.
            var owned = await SetOwnedTmdbAsync(5021);
            Assert.Null(FindDownload(await GetAsync<JsonArray>(client, url)));
            Assert.Null(FindDownload(await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions")));
            var done = await GetRequestAsync(client, "done", 5021);
            Assert.NotNull(done);
            Assert.Equal("done", (string?)done["State"]);
            Assert.Null(await GetRequestAsync(client, "downloading", 5021));
            Assert.NotNull(await GetRequestAsync(client, null, 5021));

            // A done request stays done.
            await PutRequestAsync(client, 5021, "downloading", HttpStatusCode.Conflict);
        }
        finally
        {
            await SetOwnedTmdbAsync(null);
            await ClearSavedAsync(client);
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "dl-flow");
        }
    }

    [Fact]
    public async Task Download_FailedShowsAvailableAgain()
    {
        var client = await SetUpAsync();
        try
        {
            await SetDownloadsAsync(client, true);
            await PutListAsync(client, "dl-failed", 5031);
            var filmId = Sync.FindFilm(5031)!.Id;
            var url = $"Items/{filmId:N}/Actions";

            await PostAsync<JsonObject>(client, url + "/download");
            await PutRequestAsync(client, 5031, "failed", HttpStatusCode.NoContent);

            var failed = FindDownload(await GetAsync<JsonArray>(client, url))!;
            Assert.Equal("available", (string?)failed["State"]);
            Assert.Equal("Download", (string?)failed["Label"]);
            Assert.True((bool)failed["Enabled"]!);
            Assert.NotNull(await GetRequestAsync(client, "failed", 5031));

            // Pressed again: requested again, for the app to retry.
            Assert.Equal("requested", (string?)(await PostAsync<JsonObject>(client, url + "/download"))["State"]);
            Assert.NotNull(await GetRequestAsync(client, "requested", 5031));
            Assert.Null(await GetRequestAsync(client, "failed", 5031));
        }
        finally
        {
            await ClearSavedAsync(client);
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "dl-failed");
        }
    }

    [Fact]
    public async Task Requests_AdminOnly_ForApp()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            using (var get = await userClient.GetAsync("Discovery/Requests", Ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            }

            using (var getProtected = await userClient.GetAsync("Discovery/Protected", Ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, getProtected.StatusCode);
            }

            using (var put = await userClient.PutAsJsonAsync("Discovery/Requests/5099", new { State = "downloading" }, JsonDefaults.Options, Ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
            }

            using (var settings = await userClient.PutAsJsonAsync("Discovery/Settings/Downloads", new { Enabled = true }, JsonDefaults.Options, Ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
            }

            // The admin: a bad state is a 400, a film nobody requested a 404.
            using (var badFilter = await client.GetAsync("Discovery/Requests?state=bogus", Ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
            }

            await PutRequestAsync(client, 5099, "done", HttpStatusCode.BadRequest);
            await PutRequestAsync(client, 5099, "requested", HttpStatusCode.BadRequest);
            await PutRequestAsync(client, 5099, "downloading", HttpStatusCode.NotFound);
            Assert.NotNull(await GetAsync<JsonArray>(client, "Discovery/Requests"));
        }
        finally
        {
            await UserManager.DeleteUserAsync(user.Id);
        }
    }

    [Fact]
    public async Task Protected_IncludesSavedAndActiveRequests()
    {
        var client = await SetUpAsync();
        var (user, userClient) = await CreateNonAdminAsync();
        try
        {
            await SetDownloadsAsync(client, true);
            await PutListAsync(client, "dl-protected", 5041, 5042, 5043);

            // 5041 saved by the other profile; 5042 requested by the admin, then taken out of their Saved; 5043 neither.
            await PostAsync<JsonObject>(userClient, $"Items/{Sync.FindFilm(5041)!.Id:N}/Actions/save");
            await PostAsync<JsonObject>(client, $"Items/{Sync.FindFilm(5042)!.Id:N}/Actions/download");
            await ClearSavedAsync(client);

            var ids = await GetProtectedAsync(client);
            Assert.Contains(5041, ids);
            Assert.Contains(5042, ids);
            Assert.DoesNotContain(5043, ids);

            // The list rotates out: the sync keeps the saved film and the requested one, with nobody's Saved holding it.
            await DeleteListsAsync(client, "dl-protected");
            Assert.NotNull(Sync.FindFilm(5041));
            Assert.NotNull(Sync.FindFilm(5042));
            Assert.Null(Sync.FindFilm(5043));

            // A failed request still protects; a done one doesn't.
            await PutRequestAsync(client, 5042, "failed", HttpStatusCode.NoContent);
            Assert.Contains(5042, await GetProtectedAsync(client));
            await SetOwnedTmdbAsync(5042);
            Assert.DoesNotContain(5042, await GetProtectedAsync(client));
            Assert.NotNull(await GetRequestAsync(client, "done", 5042));
        }
        finally
        {
            await SetOwnedTmdbAsync(null);
            await SetDownloadsAsync(client, false);
            await UserManager.DeleteUserAsync(user.Id);
            await DeleteListsAsync(client, "dl-protected");
        }
    }

    private static JsonObject? FindDownload(JsonArray actions)
        => actions.SingleOrDefault(a => (string?)a!["Id"] == "download")?.AsObject();

    private static int[] FilmTmdbIds(JsonObject list)
        => list["Films"]!.AsArray().Select(f => (int)f!["TmdbId"]!).ToArray();

    private static Guid[] RequestUserIds(JsonObject request)
        => request["UserIds"]!.AsArray().Select(u => Guid.Parse((string)u!)).ToArray();

    private static async Task<JsonObject> GetSavedAsync(HttpClient client)
        => (await GetAsync<JsonArray>(client, "Discovery/UserLists")).Single(l => (bool)l!["IsDefault"]!)!.AsObject();

    private static async Task ClearSavedAsync(HttpClient client)
    {
        var saved = await GetSavedAsync(client);
        foreach (var tmdbId in FilmTmdbIds(saved))
        {
            using var response = await client.DeleteAsync($"Discovery/UserLists/{(string)saved["Id"]!}/Films/{tmdbId}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task<JsonObject?> GetRequestAsync(HttpClient client, string? state, int tmdbId)
    {
        var requests = await GetAsync<JsonArray>(client, state is null ? "Discovery/Requests" : "Discovery/Requests?state=" + state);
        return requests.SingleOrDefault(r => (int)r!["TmdbId"]! == tmdbId)?.AsObject();
    }

    private static async Task<int[]> GetProtectedAsync(HttpClient client)
        => (await GetAsync<JsonObject>(client, "Discovery/Protected"))["TmdbIds"]!.AsArray().Select(i => (int)i!).ToArray();

    private static async Task PutRequestAsync(HttpClient client, int tmdbId, string state, HttpStatusCode expected)
    {
        using var response = await client.PutAsJsonAsync($"Discovery/Requests/{tmdbId}", new { State = state }, JsonDefaults.Options, Ct);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task SetDownloadsAsync(HttpClient client, bool enabled)
    {
        using var response = await client.PutAsJsonAsync("Discovery/Settings/Downloads", new { Enabled = enabled }, JsonDefaults.Options, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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

    private async Task<(User User, HttpClient Client)> CreateNonAdminAsync()
    {
        var client = await SetUpAsync();
        var name = "dl-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("Users/New", new { Name = name, Password = "dl-pw" }, JsonDefaults.Options, Ct))
        {
            create.EnsureSuccessStatusCode();
        }

        var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.AddAuthHeader(await AuthenticateAsync(userClient, name, "dl-pw"));
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
