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
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// Fork: the show page endpoint (<c>GET Shows/{id}/FullSeasons</c>) and the Get routes, over the real stores with a
/// fake TMDB. Its own host.
/// </summary>
public sealed class ShowSeasonsApiTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _root;

    public ShowSeasonsApiTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _root = Path.Combine(Path.GetTempPath(), "jellyfin-seasons-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private ISeasonStore SeasonStore => _factory.Services.GetRequiredService<ISeasonStore>();

    private IUserListStore UserLists => _factory.Services.GetRequiredService<IUserListStore>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    private FakeTmdbSeasonSource Tmdb => _factory.Services.GetRequiredService<FakeTmdbSeasonSource>();

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task FullSeasons_DiscoveryShow_EverySeasonWithStates()
    {
        var client = await SetUpAsync();
        await SetDownloadsAsync(client, true);
        try
        {
            Tmdb.Set(9101, [FakeTmdbSeasonSource.Season(1, 3), FakeTmdbSeasonSource.Season(2, 2), FakeTmdbSeasonSource.Season(3, 2, new DateOnly(2099, 1, 1))]);
            await PutListAsync(client, "ss-page", 9101);
            var show = Sync.FindSeries(9101)!;

            var page = await GetAsync<JsonObject>(client, $"Shows/{show.Id:N}/FullSeasons");

            Assert.Equal(new[] { "SeriesTmdbId", "CanGetSeries", "GetSeriesSeasonCount", "Seasons" }, page.Select(p => p.Key));
            Assert.Equal((9101, true, 2), ((int)page["SeriesTmdbId"]!, (bool)page["CanGetSeries"]!, (int)page["GetSeriesSeasonCount"]!));

            var seasons = page["Seasons"]!.AsArray();
            Assert.Equal(new[] { 1, 2, 3 }, seasons.Select(s => (int)s!["SeasonNumber"]!));
            var s1 = seasons[0]!.AsObject();
            Assert.Equal(new[] { "SeasonNumber", "Name", "State", "OnServer", "Aired", "Total", "CanGet", "Episodes" }, s1.Select(p => p.Key));
            Assert.Equal(("Season 1", "notOnServer", 0, 3, 3, true), ((string)s1["Name"]!, (string)s1["State"]!, (int)s1["OnServer"]!, (int)s1["Aired"]!, (int)s1["Total"]!, (bool)s1["CanGet"]!));
            Assert.Equal(("notOnServer", false), ((string)seasons[2]!["State"]!, (bool)seasons[2]!["CanGet"]!));

            // Null fields are left out, as everywhere in the API: here ItemId, as the episode isn't on the server.
            var e1 = s1["Episodes"]!.AsArray()[0]!.AsObject();
            Assert.Equal(
                new[] { "SeasonNumber", "EpisodeNumber", "Name", "Overview", "AirDate", "RuntimeMinutes", "StillUrl", "State" },
                e1.Select(p => p.Key));
            Assert.Equal((1, 1, "Episode 1", "Overview of S1E1", "2001-01-01", 22), ((int)e1["SeasonNumber"]!, (int)e1["EpisodeNumber"]!, (string)e1["Name"]!, (string)e1["Overview"]!, (string)e1["AirDate"]!, (int)e1["RuntimeMinutes"]!));
            Assert.Equal(("https://image.tmdb.org/t/p/w300/s1e1.jpg", "notOnServer"), ((string)e1["StillUrl"]!, (string)e1["State"]!));
            Assert.Equal("notAired", (string?)seasons[2]!["Episodes"]!.AsArray()[0]!["State"]);

            // Show downloads off: the same seasons, nothing to get.
            await SetDownloadsAsync(client, false);
            var off = await GetAsync<JsonObject>(client, $"Shows/{show.Id:N}/FullSeasons");
            Assert.False((bool)off["CanGetSeries"]!);
            Assert.All(off["Seasons"]!.AsArray(), s => Assert.False((bool)s!["CanGet"]!));

            // An unknown id and a film are 404s.
            await AssertStatusAsync(client, HttpMethod.Get, $"Shows/{Guid.NewGuid():N}/FullSeasons", HttpStatusCode.NotFound);
            var seed = await DiscoverySeed.EnsureAsync(_factory);
            await AssertStatusAsync(client, HttpMethod.Get, $"Shows/{seed.RealFilm.Id:N}/FullSeasons", HttpStatusCode.NotFound);
        }
        finally
        {
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "ss-page");
        }
    }

    [Fact]
    public async Task FullSeasons_OwnedShow_LibraryAndTmdb()
    {
        var client = await SetUpAsync();
        await SetDownloadsAsync(client, true);
        Series? owned = null;
        try
        {
            Tmdb.Set(9201, [FakeTmdbSeasonSource.Season(1, 2), FakeTmdbSeasonSource.Season(2, 2)]);
            owned = await AddLibrarySeriesAsync(9201);
            var episodeIds = AddLibrarySeason(owned, 1, 2);

            var page = await GetAsync<JsonObject>(client, $"Shows/{owned.Id:N}/FullSeasons");

            var seasons = page["Seasons"]!.AsArray();
            Assert.Equal(("onServer", false), ((string)seasons[0]!["State"]!, (bool)seasons[0]!["CanGet"]!));
            Assert.Equal(episodeIds, seasons[0]!["Episodes"]!.AsArray().Select(e => Guid.Parse((string)e!["ItemId"]!)).ToArray());
            Assert.Equal(("notOnServer", true), ((string)seasons[1]!["State"]!, (bool)seasons[1]!["CanGet"]!));
            Assert.Equal(1, (int)page["GetSeriesSeasonCount"]!);

            // The Download action is Get series on the owned show, and gets season 2 only, without saving it.
            var download = (await GetAsync<JsonArray>(client, $"Items/{owned.Id:N}/Actions")).Single(a => (string?)a!["Id"] == "download")!;
            Assert.Equal(("Get series", "available"), ((string)download["Label"]!, (string)download["State"]!));
            var performed = await SendAsync<JsonObject>(client, HttpMethod.Post, $"Items/{owned.Id:N}/Actions/download");
            Assert.Equal(("Requested", "requested"), ((string)performed["Label"]!, (string)performed["State"]!));
            Assert.Equal(new[] { 2 }, (await SeasonStore.GetAsync(9201, Ct)).Select(r => r.SeasonNumber));

            // Without a TMDB id, the library's seasons only, and the Get routes are 404s.
            owned.ProviderIds.Remove(MetadataProvider.Tmdb.ToString());
            await owned.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
            var noTmdb = await GetAsync<JsonObject>(client, $"Shows/{owned.Id:N}/FullSeasons");
            Assert.Null(noTmdb["SeriesTmdbId"]);
            Assert.False((bool)noTmdb["CanGetSeries"]!);
            Assert.Equal(new[] { 1 }, noTmdb["Seasons"]!.AsArray().Select(s => (int)s!["SeasonNumber"]!));
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{owned.Id:N}/Seasons/2/Get", HttpStatusCode.NotFound);
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{owned.Id:N}/GetAll", HttpStatusCode.NotFound);
        }
        finally
        {
            if (owned is not null)
            {
                LibraryManager.DeleteItem(owned, new DeleteOptions { DeleteFileLocation = false }, false);
            }

            await SetDownloadsAsync(client, false);
        }
    }

    [Fact]
    public async Task GetSeason_409WhenNotGettable()
    {
        var client = await SetUpAsync();
        await SetDownloadsAsync(client, true);
        try
        {
            Tmdb.Set(9301, [FakeTmdbSeasonSource.Season(0, 3), FakeTmdbSeasonSource.Season(1, 3), FakeTmdbSeasonSource.Season(2, 2, new DateOnly(2099, 1, 1))]);
            await PutListAsync(client, "ss-409", 9301);
            var show = Sync.FindSeries(9301)!;

            // Specials, a negative season, an unaired season and one TMDB doesn't list.
            foreach (var season in new[] { "0", "-1", "2", "7" })
            {
                await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/Seasons/{season}/Get", HttpStatusCode.Conflict);
            }

            // Show downloads off.
            await SetDownloadsAsync(client, false);
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/Seasons/1/Get", HttpStatusCode.Conflict);
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/GetAll", HttpStatusCode.Conflict);
            Assert.Empty(await SeasonStore.GetAsync(9301, Ct));

            // An unknown show.
            await SetDownloadsAsync(client, true);
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{Guid.NewGuid():N}/Seasons/1/Get", HttpStatusCode.NotFound);
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{Guid.NewGuid():N}/GetAll", HttpStatusCode.NotFound);
        }
        finally
        {
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "ss-409");
        }
    }

    [Fact]
    public async Task GetAll_RequestsOnlyGettable()
    {
        var client = await SetUpAsync();
        var user = await GetUserAsync(client);
        await SetDownloadsAsync(client, true);
        try
        {
            Tmdb.Set(9401, [FakeTmdbSeasonSource.Season(1, 3), FakeTmdbSeasonSource.Season(2, 3), FakeTmdbSeasonSource.Season(3, 3), FakeTmdbSeasonSource.Season(4, 2, new DateOnly(2099, 1, 1))]);
            await PutListAsync(client, "ss-all", 9401);
            var show = Sync.FindSeries(9401)!;
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/Seasons/2/Get", HttpStatusCode.NoContent);

            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/GetAll", HttpStatusCode.NoContent);

            // Seasons 1 and 3 joined 2; not the unaired 4.
            var requests = await SeasonStore.GetAsync(9401, Ct);
            Assert.Equal(new[] { 1, 2, 3 }, requests.Select(r => r.SeasonNumber));
            Assert.All(requests, r => Assert.Equal(SeasonRequest.StatePending, r.State));

            // Saved, so the show outlives its list; no series request (that's the Download action's, for older apps).
            var saved = await UserLists.GetDefaultListAsync(user.Id, Ct);
            Assert.Contains(saved.Films, f => f.TmdbId == 9401 && f.Type == DiscoveryMediaType.Series);
            Assert.Null(await _factory.Services.GetRequiredService<IDiscoveryRequestStore>().GetAsync(9401, DiscoveryMediaType.Series, Ct));

            var page = await GetAsync<JsonObject>(client, $"Shows/{show.Id:N}/FullSeasons");
            Assert.Equal((false, 0), ((bool)page["CanGetSeries"]!, (int)page["GetSeriesSeasonCount"]!));
            Assert.Equal(
                new[] { "requested", "requested", "requested", "notOnServer" },
                page["Seasons"]!.AsArray().Select(s => (string)s!["State"]!));
            Assert.Equal("coming", (string?)page["Seasons"]![0]!["Episodes"]![0]!["State"]);

            // Nothing left to get.
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/GetAll", HttpStatusCode.Conflict);

            // The Download action follows the seasons: requested, disabled.
            var download = (await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions")).Single(a => (string?)a!["Id"] == "download")!;
            Assert.Equal(("Requested", "requested", false), ((string)download["Label"]!, (string)download["State"]!, (bool)download["Enabled"]!));
        }
        finally
        {
            foreach (var season in new[] { 1, 2, 3 })
            {
                await SeasonStore.SetStateAsync(9401, season, SeasonRequest.StateHandled, null, Ct);
            }

            var saved = await UserLists.GetDefaultListAsync(user.Id, Ct);
            await UserLists.RemoveFilmAsync(user.Id, saved.Id, 9401, DiscoveryMediaType.Series, Ct);
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "ss-all");
        }
    }

    [Fact]
    public async Task GetSeason_Twice_NoDuplicate()
    {
        var client = await SetUpAsync();
        var user = await GetUserAsync(client);
        await SetDownloadsAsync(client, true);
        try
        {
            Tmdb.Set(9501, [FakeTmdbSeasonSource.Season(1, 3), FakeTmdbSeasonSource.Season(2, 3)]);
            await PutListAsync(client, "ss-twice", 9501);
            var show = Sync.FindSeries(9501)!;

            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/Seasons/1/Get", HttpStatusCode.NoContent);
            var first = Assert.Single(await SeasonStore.GetAsync(9501, Ct));

            // Requested now: a second Get is refused and changes nothing.
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/Seasons/1/Get", HttpStatusCode.Conflict);
            Assert.Equal(first, Assert.Single(await SeasonStore.GetAsync(9501, Ct)));
            Assert.Single((await UserLists.GetDefaultListAsync(user.Id, Ct)).Films, f => f.TmdbId == 9501 && f.Type == DiscoveryMediaType.Series);

            // Get series while season 1 is requested gets season 2 only.
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/GetAll", HttpStatusCode.NoContent);
            var requests = await SeasonStore.GetAsync(9501, Ct);
            Assert.Equal(new[] { 1, 2 }, requests.Select(r => r.SeasonNumber));
            Assert.Equal(first, requests[0]);

            // A failed season can be got again.
            await SeasonStore.SetStateAsync(9501, 1, SeasonRequest.StateFailed, "no release", Ct);
            await AssertStatusAsync(client, HttpMethod.Post, $"Shows/{show.Id:N}/Seasons/1/Get", HttpStatusCode.NoContent);
            Assert.Equal(SeasonRequest.StatePending, (await SeasonStore.GetAsync(9501, Ct))[0].State);
        }
        finally
        {
            await SeasonStore.SetStateAsync(9501, 1, SeasonRequest.StateHandled, null, Ct);
            await SeasonStore.SetStateAsync(9501, 2, SeasonRequest.StateHandled, null, Ct);
            var saved = await UserLists.GetDefaultListAsync(user.Id, Ct);
            await UserLists.RemoveFilmAsync(user.Id, saved.Id, 9501, DiscoveryMediaType.Series, Ct);
            await SetDownloadsAsync(client, false);
            await DeleteListsAsync(client, "ss-twice");
        }
    }

    // A real show in the seeded library (a folder on disk, never scanned), with a TMDB id.
    private async Task<Series> AddLibrarySeriesAsync(int tmdbId)
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var path = Path.Combine(_root, "owned-show-" + tmdbId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        var show = new Series
        {
            Name = "Owned Show " + tmdbId.ToString(CultureInfo.InvariantCulture),
            Path = path,
            Id = LibraryManager.GetNewItemId(path, typeof(Series)),
        };
        show.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));
        var folder = (Folder)LibraryManager.GetItemById(seed.RealFilm.ParentId)!;
        folder.AddChild(show);
        return show;
    }

    // A season folder with numbered episodes, as a scan would make them; returns the episode ids in order.
    private Guid[] AddLibrarySeason(Series series, int seasonNumber, int episodes)
    {
        var seasonPath = Path.Combine(series.Path, "Season " + seasonNumber.ToString(CultureInfo.InvariantCulture));
        var season = new Season
        {
            Name = "Season " + seasonNumber.ToString(CultureInfo.InvariantCulture),
            Path = seasonPath,
            Id = LibraryManager.GetNewItemId(seasonPath, typeof(Season)),
            IndexNumber = seasonNumber,
            SeriesId = series.Id,
            SeriesName = series.Name,
            SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
        };
        season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
        series.AddChild(season);

        return Enumerable.Range(1, episodes).Select(e =>
        {
            var path = Path.Combine(seasonPath, $"S{seasonNumber:00}E{e:00}.mkv");
            var episode = new Episode
            {
                Name = "Library episode " + e.ToString(CultureInfo.InvariantCulture),
                Path = path,
                Id = LibraryManager.GetNewItemId(path, typeof(Episode)),
                IndexNumber = e,
                ParentIndexNumber = seasonNumber,
                SeriesId = series.Id,
                SeasonId = season.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
            };
            episode.PresentationUniqueKey = episode.CreatePresentationUniqueKey();
            season.AddChild(episode);
            return episode.Id;
        }).ToArray();
    }

    private static async Task PutListAsync(HttpClient client, string listId, params int[] tmdbIds)
    {
        var films = tmdbIds.Select(t => new { TmdbId = t }).ToArray();
        using var response = await client.PutAsJsonAsync("Discovery/Lists/" + listId, new { Title = listId, Source = "tests", Films = films, ItemType = "Series" }, JsonDefaults.Options, Ct);
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

    private static async Task SetDownloadsAsync(HttpClient client, bool seriesEnabled)
    {
        using var response = await client.PutAsJsonAsync("Discovery/Settings/Downloads", new { Enabled = false, SeriesEnabled = seriesEnabled }, JsonDefaults.Options, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task AssertStatusAsync(HttpClient client, HttpMethod method, string url, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url);
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == expected, $"{method} {url}: {response.StatusCode}, expected {expected}");
    }

    private static Task<T> GetAsync<T>(HttpClient client, string url)
        => SendAsync<T>(client, HttpMethod.Get, url);

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url}: {response.StatusCode}");
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, Ct);
        Assert.NotNull(value);
        return value;
    }

    private async Task<User> GetUserAsync(HttpClient client)
    {
        var id = Guid.Parse((string)(await GetAsync<JsonObject>(client, "Users/Me"))["Id"]!);
        return _factory.Services.GetRequiredService<IUserManager>().GetUserById(id)!;
    }

    private async Task<HttpClient> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        return client;
    }
}
