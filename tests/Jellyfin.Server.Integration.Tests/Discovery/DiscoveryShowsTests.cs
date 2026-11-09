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
using Jellyfin.Api.Models.HomeRowDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.HomeRows;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// Show discovery: virtual Series for Series lists, hidden like the virtual films, with trailers, Save and Download
/// keyed by type, done detection by an owned Series, and the show-discovery row. Its own host.
/// </summary>
public sealed class DiscoveryShowsTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _trailerRoot;

    public DiscoveryShowsTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
        _trailerRoot = Path.Combine(Path.GetTempPath(), "jellyfin-shows-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task SeriesList_CreatesHiddenVirtualSeries_NoSeasons()
    {
        var client = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var user = await GetUserAsync(client);
        try
        {
            await PutListAsync(client, "sh-shows", "Series", 8101, 8102);

            // A pathless virtual Series per show, in the discovery folder; no film for the same ids.
            var show = Assert.IsType<Series>(Sync.FindSeries(8101));
            Assert.True(show.IsVirtualItem);
            Assert.True(string.IsNullOrEmpty(show.Path));
            Assert.Equal(seed.FolderId, show.ParentId);
            Assert.Equal("8101", show.GetProviderId(MetadataProvider.Tmdb));
            Assert.Equal(show.Id, Sync.FindTitle(8101, DiscoveryMediaType.Series)!.Id);
            Assert.NotNull(Sync.FindSeries(8102));
            Assert.Null(Sync.FindFilm(8101));
            Assert.Null(Sync.FindTitle(8101, DiscoveryMediaType.Movie));

            // It never plays and has no seasons or episodes.
            Assert.Equal(PlayAccess.None, show.GetPlayAccess(user));
            Assert.Empty(LibraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, ParentId = show.Id }));
            Assert.Empty(LibraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, AncestorIds = [show.Id] }));
            var dto = await GetAsync<JsonObject>(client, $"Items/{show.Id:N}?userId={user.Id:N}");
            Assert.Equal("Series", (string?)dto["Type"]);
            Assert.Equal("None", (string?)dto["PlayAccess"]);
            Assert.Empty((await GetAsync<JsonObject>(client, $"Shows/{show.Id:N}/Seasons?userId={user.Id:N}"))["Items"]!.AsArray());

            // A movie list with the same id makes a film beside the show; each goes with its own list.
            await PutListAsync(client, "sh-films", null, 8101);
            Assert.NotNull(Sync.FindFilm(8101));
            Assert.NotEqual(Sync.FindFilm(8101)!.Id, show.Id);
            await DeleteListsAsync(client, "sh-films");
            Assert.Null(Sync.FindFilm(8101));
            Assert.NotNull(Sync.FindSeries(8101));

            // The show leaves with its list.
            await PutListAsync(client, "sh-shows", "Series", 8102);
            Assert.Null(Sync.FindSeries(8101));
            Assert.Null(LibraryManager.GetItemById(show.Id));
            Assert.NotNull(Sync.FindSeries(8102));
        }
        finally
        {
            await DeleteListsAsync(client, "sh-shows", "sh-films");
        }

        Assert.Null(Sync.FindSeries(8102));
    }

    [Fact]
    public async Task VirtualSeries_HiddenFromSearchAndLibraries()
    {
        var client = await SetUpAsync();
        var user = await GetUserAsync(client);
        try
        {
            await PutListAsync(client, "sh-hidden", "Series", 8201);
            var show = Sync.FindSeries(8201)!;
            show.Name = "Zebrafish Hidden Show";
            await show.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, Ct);
            await LibraryManager.UpdatePeopleAsync(show, [new PersonInfo { Name = "Zebrafish Show Actor", Type = PersonKind.Actor }], Ct);

            // Libraries, search, counts and Latest leave it out.
            var series = await GetAsync<JsonObject>(client, $"Items?recursive=true&includeItemTypes=Series&userId={user.Id:N}");
            var searched = await GetAsync<JsonObject>(client, $"Items?recursive=true&searchTerm=Zebrafish&userId={user.Id:N}");
            var noUser = await GetAsync<JsonObject>(client, "Items?recursive=true&includeItemTypes=Series");
            var hints = await GetAsync<JsonObject>(client, $"Search/Hints?searchTerm=Zebrafish&userId={user.Id:N}");
            var counts = await GetAsync<JsonObject>(client, $"Items/Counts?userId={user.Id:N}");
            var latest = await GetAsync<JsonArray>(client, $"Items/Latest?userId={user.Id:N}&limit=100&groupItems=false");
            var persons = await GetAsync<JsonObject>(client, $"Persons?userId={user.Id:N}");
            var searchedPersons = await GetAsync<JsonObject>(client, $"Persons?userId={user.Id:N}&searchTerm=Zebrafish");
            AssertHidden(show, series["Items"]!.AsArray());
            AssertHidden(show, searched["Items"]!.AsArray());
            AssertHidden(show, noUser["Items"]!.AsArray());
            Assert.Empty(hints["SearchHints"]!.AsArray());
            Assert.Equal(0, (int)counts["SeriesCount"]!);
            AssertHidden(show, latest);
            Assert.DoesNotContain(persons["Items"]!.AsArray(), p => (string?)p!["Name"] == "Zebrafish Show Actor");
            Assert.Empty(searchedPersons["Items"]!.AsArray());
            Assert.DoesNotContain(
                LibraryManager.GetItemList(new InternalItemsQuery { Recursive = true, IncludeItemTypes = [BaseItemKind.Series] }),
                i => i.Id.Equals(show.Id));

            // Its own page still resolves by id, as a virtual film's does.
            Assert.Equal(show.Id, Guid.Parse((string)(await GetAsync<JsonObject>(client, $"Items/{show.Id:N}?userId={user.Id:N}"))["Id"]!));
        }
        finally
        {
            await DeleteListsAsync(client, "sh-hidden");
        }
    }

    [Fact]
    public async Task WatchTrailer_OnSeries()
    {
        var client = await SetUpAsync();
        var user = await GetUserAsync(client);
        var trailerPath = Path.Combine(_trailerRoot, "show-8301.mp4");
        await File.WriteAllBytesAsync(trailerPath, new byte[1024], Ct);
        try
        {
            await PutListAsync(client, "sh-trailer", "Series", 8301);
            var show = Sync.FindSeries(8301)!;
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions"), a => (string?)a!["Id"] == "watch-trailer");

            // The movie type knows no 8301; the show takes the trailer.
            Assert.Equal(HttpStatusCode.NotFound, await PutTrailerAsync(client, "Discovery/Films/8301/Trailer", trailerPath));
            Assert.Equal(HttpStatusCode.NoContent, await PutTrailerAsync(client, "Discovery/Films/8301/Trailer?type=Series", trailerPath));
            var trailer = Assert.Single(show.GetExtras([ExtraType.Trailer]));
            Assert.Equal(show.Id, trailer.OwnerId);

            // Watch trailer plays it, named after the show; the list page and its Watch trailers have the show.
            var action = FindAction(await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions"), "watch-trailer");
            Assert.Equal("play-queue", (string?)action["Kind"]);
            var queue = await GetAsync<JsonObject>(client, $"Items/{show.Id:N}/Actions/watch-trailer/Queue");
            var entry = Assert.Single(queue["Items"]!.AsArray())!;
            Assert.Equal(trailer.Id, Guid.Parse((string)entry["Id"]!));
            Assert.Equal(show.Id, Guid.Parse((string)entry["FilmId"]!));
            var page = await GetAsync<JsonObject>(client, "Discovery/Lists/sh-trailer/Page");
            Assert.Equal(show.Id, Guid.Parse((string)Assert.Single(page["Items"]!.AsArray())!["Id"]!));
            Assert.Contains(await GetAsync<JsonArray>(client, "Discovery/Lists/sh-trailer/Actions"), a => (string?)a!["Id"] == "watch-trailers");
            var listQueue = await GetAsync<JsonObject>(client, "Discovery/Lists/sh-trailer/Actions/watch-trailers/Queue");
            Assert.Equal(trailer.Id, Guid.Parse((string)Assert.Single(listQueue["ItemIds"]!.AsArray())!));
            Assert.Equal(1, (int)(await GetListAsync(client, "sh-trailer"))["ReadyFilms"]!);

            // A trailer file of a show is a discovery trailer too: setting it again replaces nothing else.
            Assert.Equal(HttpStatusCode.NoContent, await PutTrailerAsync(client, "Discovery/Films/8301/Trailer?type=Series", trailerPath));
            Assert.Single(show.GetExtras([ExtraType.Trailer]));

            // The movie-typed DELETE leaves the show's trailer; the Series one removes it.
            using (var movieDelete = await client.DeleteAsync("Discovery/Films/8301/Trailer", Ct))
            {
                Assert.Equal(HttpStatusCode.NoContent, movieDelete.StatusCode);
            }

            Assert.Single(show.GetExtras([ExtraType.Trailer]));
            using (var delete = await client.DeleteAsync("Discovery/Films/8301/Trailer?type=Series", Ct))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            Assert.Empty(show.GetExtras([ExtraType.Trailer]));
            Assert.True(File.Exists(trailerPath));
        }
        finally
        {
            await DeleteListsAsync(client, "sh-trailer");
        }
    }

    [Fact]
    public async Task SaveAndDownload_OnSeries_KeyedBySeries()
    {
        var client = await SetUpAsync();
        var user = await GetUserAsync(client);
        await SetDownloadsAsync(client, true, true);
        try
        {
            // A film and a show with one TMDB id.
            await PutListAsync(client, "sh-save-films", null, 8401);
            await PutListAsync(client, "sh-save-shows", "Series", 8401);
            var film = Sync.FindFilm(8401)!;
            var show = Sync.FindSeries(8401)!;

            var showActions = await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions");
            Assert.Equal("off", (string?)FindAction(showActions, "save")["State"]);
            Assert.Equal("available", (string?)FindAction(showActions, "download")["State"]);

            // Save on the show saves the show only.
            var saved = await PostAsync<JsonObject>(client, $"Items/{show.Id:N}/Actions/save");
            Assert.Equal("on", (string?)saved["State"]);
            var films = (await GetSavedAsync(client))["Films"]!.AsArray();
            var savedShow = Assert.Single(films)!;
            Assert.Equal((8401, "Series"), ((int)savedShow["TmdbId"]!, (string)savedShow["Type"]!));
            Assert.Equal("off", (string?)FindAction(await GetAsync<JsonArray>(client, $"Items/{film.Id:N}/Actions"), "save")["State"]);

            // Download on the show: a Series request, saved as the show; the film has none.
            var requested = await PostAsync<JsonObject>(client, $"Items/{show.Id:N}/Actions/download");
            Assert.Equal("requested", (string?)requested["State"]);
            Assert.Equal(DiscoveryMediaType.Series, (await Requests.GetAsync(8401, DiscoveryMediaType.Series, Ct))!.Type);
            Assert.Null(await Requests.GetAsync(8401, Ct));
            Assert.Equal("available", (string?)FindAction(await GetAsync<JsonArray>(client, $"Items/{film.Id:N}/Actions"), "download")["State"]);
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, "Discovery/Requests"), r => (int)r!["TmdbId"]! == 8401);
            var seriesRequest = Assert.Single(await GetAsync<JsonArray>(client, "Discovery/Requests?type=Series"), r => (int)r!["TmdbId"]! == 8401)!;
            Assert.Equal("Series", (string?)seriesRequest["Type"]);
            var protectedItems = (await GetAsync<JsonObject>(client, "Discovery/Protected"))["Items"]!.AsArray()
                .Select(i => ((int)i!["TmdbId"]!, (string)i["Type"]!)).ToArray();
            Assert.Contains((8401, "Series"), protectedItems);
            Assert.DoesNotContain((8401, "Movie"), protectedItems);

            // Saved and requested, the show outlives its list; the film does not.
            await DeleteListsAsync(client, "sh-save-films", "sh-save-shows");
            Assert.Null(Sync.FindFilm(8401));
            Assert.NotNull(Sync.FindSeries(8401));

            // The Saved row and page show it.
            var savedList = await GetSavedAsync(client);
            var page = await GetAsync<JsonObject>(client, $"Discovery/Lists/{(string)savedList["PageListId"]!}/Page");
            Assert.Equal(show.Id, Guid.Parse((string)Assert.Single(page["Items"]!.AsArray())!["Id"]!));

            // The user-list routes take the show by type; the movie type does not know it.
            var savedId = (string)savedList["Id"]!;
            await SendAsync<JsonObject>(client, HttpMethod.Delete, $"Discovery/UserLists/{savedId}/Films/8401?type=Series");
            using (var asMovie = await client.PostAsync($"Discovery/UserLists/{savedId}/Films/8401", null, Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, asMovie.StatusCode);
            }

            var added = await SendAsync<JsonObject>(client, HttpMethod.Post, $"Discovery/UserLists/{savedId}/Films/8401?type=Series");
            Assert.Equal("Series", (string?)Assert.Single(added["Films"]!.AsArray())!["Type"]);
        }
        finally
        {
            await Requests.SetStateAsync(8401, DiscoveryMediaType.Series, DiscoveryRequest.StateDone, Ct);
            var saved = await UserLists.GetDefaultListAsync(user.Id, Ct);
            await UserLists.RemoveFilmAsync(user.Id, saved.Id, 8401, DiscoveryMediaType.Series, Ct);
            await UserLists.RemoveFilmAsync(user.Id, saved.Id, 8401, Ct);
            await DeleteListsAsync(client, "sh-save-films", "sh-save-shows");
            await SetDownloadsAsync(client, false, false);
        }
    }

    [Fact]
    public async Task Downloads_EnabledPerType()
    {
        var client = await SetUpAsync();
        try
        {
            await PutListAsync(client, "sh-dl-films", null, 8451);
            await PutListAsync(client, "sh-dl-shows", "Series", 8451);
            var film = Sync.FindFilm(8451)!;
            var show = Sync.FindSeries(8451)!;

            // An app that knows no shows sends Enabled only: films download, shows don't.
            await SetDownloadsAsync(client, true);
            var setting = await GetAsync<JsonObject>(client, "Discovery/Settings/Downloads");
            Assert.True((bool)setting["Enabled"]!);
            Assert.False((bool)setting["SeriesEnabled"]!);
            Assert.Contains(await GetAsync<JsonArray>(client, $"Items/{film.Id:N}/Actions"), a => (string?)a!["Id"] == "download");
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions"), a => (string?)a!["Id"] == "download");
            using (var refused = await client.PostAsync($"Items/{show.Id:N}/Actions/download", null, Ct))
            {
                Assert.False(refused.IsSuccessStatusCode);
            }

            Assert.Null(await Requests.GetAsync(8451, DiscoveryMediaType.Series, Ct));

            // Shows only.
            await SetDownloadsAsync(client, false, true);
            setting = await GetAsync<JsonObject>(client, "Discovery/Settings/Downloads");
            Assert.False((bool)setting["Enabled"]!);
            Assert.True((bool)setting["SeriesEnabled"]!);
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{film.Id:N}/Actions"), a => (string?)a!["Id"] == "download");
            Assert.Contains(await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions"), a => (string?)a!["Id"] == "download");
        }
        finally
        {
            await SetDownloadsAsync(client, false, false);
            await DeleteListsAsync(client, "sh-dl-films", "sh-dl-shows");
        }
    }

    [Fact]
    public async Task OwnedSeries_MarksRequestDone()
    {
        var client = await SetUpAsync();
        var user = await GetUserAsync(client);
        await SetDownloadsAsync(client, true, true);
        Series? owned = null;
        try
        {
            await PutListAsync(client, "sh-owned", "Series", 8501);
            var show = Sync.FindSeries(8501)!;
            await PostAsync<JsonObject>(client, $"Items/{show.Id:N}/Actions/download");
            await Requests.RequestAsync(8501, user.Id, Ct);

            // A library film with that id is not the show.
            Assert.Equal("requested", (string?)(await GetRequestAsync(client, 8501, "Series"))!["State"]);

            // The show arrives in a library: the Series request is done, the film's is not.
            owned = await AddLibrarySeriesAsync(8501);
            Assert.Equal("done", (string?)(await GetRequestAsync(client, 8501, "Series"))!["State"]);
            Assert.Equal("requested", (string?)(await GetRequestAsync(client, 8501, "Movie"))!["State"]);
            Assert.Equal(owned.Id, Sync.FindOwnedTitle(8501, null, DiscoveryMediaType.Series)!.Id);
            Assert.Null(Sync.FindOwnedTitle(8501, null, DiscoveryMediaType.Movie));
            Assert.DoesNotContain(8501, await Requests.GetActiveTmdbIdsAsync(DiscoveryMediaType.Series, Ct));
            Assert.Contains(8501, await Requests.GetActiveTmdbIdsAsync(Ct));

            // The show's virtual copy offers no Download now, and a row shows the owned show.
            Assert.DoesNotContain(await GetAsync<JsonArray>(client, $"Items/{show.Id:N}/Actions"), a => (string?)a!["Id"] == "download");
            var page = await GetAsync<JsonObject>(client, "Discovery/Lists/sh-owned/Page");
            Assert.Equal(owned.Id, Guid.Parse((string)Assert.Single(page["Items"]!.AsArray())!["Id"]!));
        }
        finally
        {
            if (owned is not null)
            {
                LibraryManager.DeleteItem(owned, new DeleteOptions { DeleteFileLocation = false }, false);
            }

            await Requests.SetStateAsync(8501, DiscoveryRequest.StateDone, Ct);
            var saved = await UserLists.GetDefaultListAsync(user.Id, Ct);
            await UserLists.RemoveFilmAsync(user.Id, saved.Id, 8501, DiscoveryMediaType.Series, Ct);
            await DeleteListsAsync(client, "sh-owned");
            await SetDownloadsAsync(client, false, false);
        }
    }

    [Fact]
    public async Task ShowDiscoveryRow_PicksOnlySeriesLists()
    {
        var client = await SetUpAsync();
        var showRow = Guid.NewGuid();
        try
        {
            await PutListAsync(client, "sh-row-films", null, 8601);
            await PutListAsync(client, "sh-row-shows", "Series", 8601, 8602);
            await PutLayoutAsync(client, Row(showRow, HomeRowProviderIds.ShowDiscovery, 3));

            // The provider is offered, named Show discovery, with the Movie discovery settings.
            var providers = await GetAsync<JsonObject>(client, "HomeRows/Providers");
            var provider = providers["Providers"]!.AsArray().Single(p => (string?)p!["Id"] == HomeRowProviderIds.ShowDiscovery)!;
            Assert.Equal("Show discovery", (string?)provider["Name"]);
            var movieProvider = providers["Providers"]!.AsArray().Single(p => (string?)p!["Id"] == HomeRowProviderIds.MovieDiscovery)!;
            Assert.Equal(SettingKeys(movieProvider), SettingKeys(provider));

            // Only the show list is picked; its row shows the shows.
            var expanded = await GetConfigurationAsync(client, "?expand=true");
            var derived = Assert.Single(expanded.Rows);
            Assert.Equal(HomeRowProviderIds.ShowDiscovery, derived.Provider);
            var items = await GetRowItemsAsync(client, derived.Id);
            Assert.Equal("sh-row-shows", (string?)items["Presentation"]!["PageListId"]);
            Assert.Equal(
                new[] { Sync.FindSeries(8601)!.Id, Sync.FindSeries(8602)!.Id },
                items["Items"]!.AsArray().Select(i => Guid.Parse((string)i!["Id"]!)));

            // The picks API: untyped (and Movie) has no show; Series has the show list's ids.
            var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var moviePicks = await GetAsync<JsonObject>(client, $"Discovery/Picks?date={today}");
            var showPicks = await GetAsync<JsonObject>(client, $"Discovery/Picks?date={today}&type=Series");
            Assert.Empty(moviePicks["TmdbIds"]!.AsArray());
            Assert.Equal(new[] { 8601, 8602 }, showPicks["TmdbIds"]!.AsArray().Select(i => (int)i!));
            Assert.Equal("Series", (string?)showPicks["Type"]);
            Assert.Equal(
                new[] { (8601, "Series"), (8602, "Series") },
                showPicks["Items"]!.AsArray().Select(i => ((int)i!["TmdbId"]!, (string)i["Type"]!)));
            Assert.Equal("Movie", (string?)moviePicks["Type"]);
            Assert.Empty(moviePicks["Items"]!.AsArray());

            // Films: untyped is the films only, Series the shows.
            var filmStatus = await GetAsync<JsonArray>(client, "Discovery/Films");
            var showStatus = await GetAsync<JsonArray>(client, "Discovery/Films?type=Series");
            Assert.Equal(Sync.FindFilm(8601)!.Id, Guid.Parse((string)Assert.Single(filmStatus, f => (int)f!["TmdbId"]! == 8601)!["ItemId"]!));
            Assert.Equal(Sync.FindSeries(8601)!.Id, Guid.Parse((string)Assert.Single(showStatus, f => (int)f!["TmdbId"]! == 8601)!["ItemId"]!));
            Assert.DoesNotContain(filmStatus, f => (int)f!["TmdbId"]! == 8602);
            Assert.All(filmStatus, f => Assert.Equal("Movie", (string?)f!["Type"]));
            Assert.Equal(2, showStatus.Count);
            Assert.All(showStatus, f => Assert.Equal("Series", (string?)f!["Type"]));
            Assert.All(showStatus, f => Assert.False((bool)f!["HasTrailer"]!));
        }
        finally
        {
            await PutLayoutAsync(client);
            await DeleteListsAsync(client, "sh-row-films", "sh-row-shows");
        }
    }

    [Fact]
    public async Task MovieDiscoveryRow_IgnoresSeriesLists()
    {
        var client = await SetUpAsync();
        var movieRow = Guid.NewGuid();
        var showRow = Guid.NewGuid();
        try
        {
            await PutListAsync(client, "sh-mrow-shows", "Series", 8701);
            await PutLayoutAsync(client, Row(movieRow, HomeRowProviderIds.MovieDiscovery, 3));

            // A Movie discovery row with only a show list has nothing to pick.
            Assert.Empty((await GetConfigurationAsync(client, "?expand=true")).Rows);
            Assert.Empty((await GetRowItemsAsync(client, movieRow))["Items"]!.AsArray());

            // With a film list too, it picks the film list only, beside a show row picking the show list.
            await PutListAsync(client, "sh-mrow-films", null, 8701);
            await PutLayoutAsync(client, Row(movieRow, HomeRowProviderIds.MovieDiscovery, 3), Row(showRow, HomeRowProviderIds.ShowDiscovery, 3));
            var expanded = await GetConfigurationAsync(client, "?expand=true");
            Assert.Equal(new[] { HomeRowProviderIds.MovieDiscovery, HomeRowProviderIds.ShowDiscovery }, expanded.Rows.Select(r => r.Provider));
            var filmItems = await GetRowItemsAsync(client, expanded.Rows[0].Id);
            var showItems = await GetRowItemsAsync(client, expanded.Rows[1].Id);
            Assert.Equal("sh-mrow-films", (string?)filmItems["Presentation"]!["PageListId"]);
            Assert.Equal(Sync.FindFilm(8701)!.Id, Guid.Parse((string)Assert.Single(filmItems["Items"]!.AsArray())!["Id"]!));
            Assert.Equal("sh-mrow-shows", (string?)showItems["Presentation"]!["PageListId"]);
            Assert.Equal(Sync.FindSeries(8701)!.Id, Guid.Parse((string)Assert.Single(showItems["Items"]!.AsArray())!["Id"]!));

            // Not in the default layout.
            await PutLayoutAsync(client);
            using (var reset = await client.DeleteAsync("HomeRows/Configuration", Ct))
            {
                Assert.True(reset.IsSuccessStatusCode, $"DELETE HomeRows/Configuration: {reset.StatusCode}");
            }

            Assert.DoesNotContain((await GetConfigurationAsync(client, string.Empty)).Rows, r => r.Provider == HomeRowProviderIds.ShowDiscovery);
        }
        finally
        {
            await DeleteListsAsync(client, "sh-mrow-shows", "sh-mrow-films");
        }
    }

    private static void AssertHidden(Series show, JsonArray items)
        => Assert.DoesNotContain(items, i => Guid.Parse((string)i!["Id"]!).Equals(show.Id));

    private static string[] SettingKeys(JsonNode provider)
        => provider["Defaults"]!.AsObject().Select(p => p.Key).ToArray();

    private static JsonObject FindAction(JsonArray actions, string id)
        => actions.Single(a => (string?)a!["Id"] == id)!.AsObject();

    private static HomeRowInstanceDto Row(Guid id, string provider, int maxRows)
        => new(id, provider, null, new JsonObject { ["maxRows"] = maxRows, ["requireTrailer"] = false }, true, false);

    private static async Task PutLayoutAsync(HttpClient client, params HomeRowInstanceDto[] rows)
    {
        using var put = await client.PutAsJsonAsync("HomeRows/Configuration", new HomeRowConfigurationUpdateDto(rows), JsonDefaults.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private static async Task<HomeRowConfigurationDto> GetConfigurationAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("HomeRows/Configuration" + query, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HomeRowConfigurationDto>(JsonDefaults.Options, Ct);
        Assert.NotNull(dto);
        return dto;
    }

    private static Task<JsonObject> GetRowItemsAsync(HttpClient client, Guid rowId)
        => GetAsync<JsonObject>(client, $"HomeRows/{rowId:D}/Items?limit=100");

    private static async Task<JsonObject> GetListAsync(HttpClient client, string listId)
        => (await GetAsync<JsonArray>(client, "Discovery/Lists")).Single(l => (string?)l!["Id"] == listId)!.AsObject();

    private static async Task<JsonObject> GetSavedAsync(HttpClient client)
        => (await GetAsync<JsonArray>(client, "Discovery/UserLists")).Single(l => (bool)l!["IsDefault"]!)!.AsObject();

    private static async Task<JsonObject?> GetRequestAsync(HttpClient client, int tmdbId, string type)
        => (await GetAsync<JsonArray>(client, "Discovery/Requests?type=" + type))
            .SingleOrDefault(r => (int)r!["TmdbId"]! == tmdbId && (string?)r["Type"] == type)?.AsObject();

    private static async Task SetDownloadsAsync(HttpClient client, bool enabled, bool? seriesEnabled = null)
    {
        using var response = seriesEnabled is { } series
            ? await client.PutAsJsonAsync("Discovery/Settings/Downloads", new { Enabled = enabled, SeriesEnabled = series }, JsonDefaults.Options, Ct)
            : await client.PutAsJsonAsync("Discovery/Settings/Downloads", new { Enabled = enabled }, JsonDefaults.Options, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpStatusCode> PutTrailerAsync(HttpClient client, string url, string path)
    {
        using var response = await client.PutAsJsonAsync(url, new { Path = path }, JsonDefaults.Options, Ct);
        return response.StatusCode;
    }

    private async Task<User> GetUserAsync(HttpClient client)
    {
        var id = Guid.Parse((string)(await GetAsync<JsonObject>(client, "Users/Me"))["Id"]!);
        return _factory.Services.GetRequiredService<IUserManager>().GetUserById(id)!;
    }

    // A real show in the seeded library (a folder on disk, never scanned), with a TMDB id.
    private async Task<Series> AddLibrarySeriesAsync(int tmdbId)
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var path = Path.Combine(_trailerRoot, "owned-show-" + tmdbId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        var show = new Series
        {
            Name = "Owned Show " + tmdbId.ToString(CultureInfo.InvariantCulture),
            Path = path,
            Id = LibraryManager.GetNewItemId(path, typeof(Series)),
        };
        show.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));
        // Under the library's physical folder, where the scanner puts the real film, so the user's library query sees it.
        var folder = (Folder)LibraryManager.GetItemById(seed.RealFilm.ParentId)!;
        folder.AddChild(show);
        return show;
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

    private static Task<T> GetAsync<T>(HttpClient client, string url)
        => SendAsync<T>(client, HttpMethod.Get, url);

    private static Task<T> PostAsync<T>(HttpClient client, string url)
        => SendAsync<T>(client, HttpMethod.Post, url);

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
