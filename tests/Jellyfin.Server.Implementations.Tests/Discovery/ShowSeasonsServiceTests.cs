using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

/// <summary>
/// Fork: the show page service, with a fake TMDB source and a fake library.
/// </summary>
public class ShowSeasonsServiceTests
{
    private const int Show = 1400;

    private static readonly DateOnly _today = new(2026, 11, 11);

    private readonly User _user = new("tester", "Default", "Default");
    private readonly Mock<ITmdbSeasonSource> _tmdb = new();
    private readonly Mock<ISeasonStore> _seasons = new();
    private readonly Mock<IDiscoveryRequestStore> _requests = new();
    private readonly Mock<IUserListStore> _lists = new();
    private readonly Mock<IDiscoveryFilmSync> _sync = new();
    private readonly List<LibraryEpisode> _library = [];
    private readonly UserList _saved = new(Guid.NewGuid(), Guid.NewGuid(), "Saved", true, []);

    public ShowSeasonsServiceTests()
    {
        _requests.Setup(r => r.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _seasons.Setup(s => s.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _seasons.Setup(s => s.GetSonarrStatusAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _lists.Setup(l => l.GetDefaultListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(_saved);
        _sync.Setup(s => s.RunExclusiveAsync(It.IsAny<Func<Task<ShowSeasonsGetResult>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task<ShowSeasonsGetResult>> work, CancellationToken _) => work());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShowSeasonsService Create(TimeSpan? tmdbTimeout = null)
        => new(
            _tmdb.Object,
            _seasons.Object,
            _requests.Object,
            _lists.Object,
            _sync.Object,
            NullLogger<ShowSeasonsService>.Instance,
            (_, _) => _library,
            () => _today,
            tmdbTimeout ?? TimeSpan.FromSeconds(5));

    private static Series Series(int? tmdbId)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        if (tmdbId is { } id)
        {
            series.SetProviderId(MetadataProvider.Tmdb, id.ToString(CultureInfo.InvariantCulture));
        }

        return series;
    }

    private static TmdbSeason Tmdb(int season, int episodes)
        => new(season, "Season " + season, Enumerable.Range(1, episodes).Select(e => new TmdbEpisode(e, "E" + e, null, new DateOnly(2020, 1, 1), 22, null)).ToList());

    private void AddLibrary(int season, int episodes)
        => _library.AddRange(Enumerable.Range(1, episodes).Select(e => new LibraryEpisode(Guid.NewGuid(), season, e, null, "E" + e, null, null, 22, null)));

    private void SetTmdb(params TmdbSeason[] seasons)
        => _tmdb.Setup(t => t.GetSeasonsAsync(Show, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(seasons);

    [Fact]
    public async Task TmdbThrows_PageStillLoads_LibraryOnly()
    {
        AddLibrary(1, 3);
        AddLibrary(2, 2);
        _tmdb.Setup(t => t.GetSeasonsAsync(Show, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("TMDB is down"));

        var page = await Create().GetPageAsync(_user, Series(Show), Ct);

        Assert.Equal(Show, page.SeriesTmdbId);
        Assert.False(page.CanGetSeries);
        Assert.Equal(0, page.GetSeriesSeasonCount);
        Assert.Equal(new[] { 1, 2 }, page.Seasons.Select(s => s.SeasonNumber));
        Assert.All(page.Seasons, s => Assert.Equal((SeasonView.StateOnServer, false), (s.State, s.CanGet)));

        // Get is refused, and nothing is requested.
        Assert.Equal(ShowSeasonsGetResult.NotGettable, await Create().GetSeasonsAsync(_user, Series(Show), null, false, Ct));
        _seasons.Verify(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TmdbTimesOut_PageStillLoads_LibraryOnly()
    {
        AddLibrary(1, 3);
        _tmdb.Setup(t => t.GetSeasonsAsync(Show, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, string? _, string? _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            });

        var page = await Create(TimeSpan.FromMilliseconds(50)).GetPageAsync(_user, Series(Show), Ct);

        Assert.False(page.CanGetSeries);
        Assert.Equal(SeasonView.StateOnServer, Assert.Single(page.Seasons).State);
    }

    [Fact]
    public async Task TmdbIgnoresToken_PageStillLoadsWithinCap()
    {
        AddLibrary(1, 3);

        // A source that never finishes and never looks at its token (as TMDB's client config fetch doesn't).
        var never = new TaskCompletionSource<IReadOnlyList<TmdbSeason>?>();
        _tmdb.Setup(t => t.GetSeasonsAsync(Show, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(never.Task);

        var page = await Create(TimeSpan.FromMilliseconds(50)).GetPageAsync(_user, Series(Show), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(page.CanGetSeries);
        Assert.Equal(SeasonView.StateOnServer, Assert.Single(page.Seasons).State);
    }

    [Fact]
    public async Task NoTmdbId_LibraryOnly_TmdbNotAsked()
    {
        AddLibrary(1, 3);

        var service = Create();
        var page = await service.GetPageAsync(_user, Series(null), Ct);

        Assert.Null(page.SeriesTmdbId);
        Assert.False(page.CanGetSeries);
        Assert.Equal(SeasonView.StateOnServer, Assert.Single(page.Seasons).State);
        Assert.Equal(ShowSeasonsGetResult.NoTmdbId, await service.GetSeasonsAsync(_user, Series(null), [1], false, Ct));
        _tmdb.Verify(t => t.GetSeasonsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Page_UsesRequestsSonarrAndTheDownloadsSetting()
    {
        AddLibrary(1, 10);
        SetTmdb(Tmdb(1, 10), Tmdb(2, 10), Tmdb(3, 10));
        _seasons.Setup(s => s.GetAsync(Show, It.IsAny<CancellationToken>())).ReturnsAsync([new SeasonRequest(Show, 2, SeasonRequest.StatePending, null, DateTime.UtcNow)]);

        var page = await Create().GetPageAsync(_user, Series(Show), Ct);

        Assert.Equal(new[] { SeasonView.StateOnServer, SeasonView.StateRequested, SeasonView.StateNotOnServer }, page.Seasons.Select(s => s.State));
        Assert.True(page.CanGetSeries);
        Assert.Equal(1, page.GetSeriesSeasonCount);

        // Show downloads off: the same states, nothing to get.
        _requests.Setup(r => r.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var off = await Create().GetPageAsync(_user, Series(Show), Ct);
        Assert.False(off.CanGetSeries);
        Assert.Equal(0, off.GetSeriesSeasonCount);
    }

    [Fact]
    public async Task GetSeasons_OwnedShow_RequestsOnlyGettable_NotSaved()
    {
        AddLibrary(1, 10);
        AddLibrary(0, 2);
        SetTmdb(Tmdb(1, 10), Tmdb(2, 10), Tmdb(3, 10));

        var service = Create();
        var series = Series(Show);

        Assert.Equal(ShowSeasonsGetResult.NotGettable, await service.GetSeasonsAsync(_user, series, [0], false, Ct));
        Assert.Equal(ShowSeasonsGetResult.NotGettable, await service.GetSeasonsAsync(_user, series, [1], false, Ct));
        Assert.Equal(ShowSeasonsGetResult.NotGettable, await service.GetSeasonsAsync(_user, series, [-1], false, Ct));
        Assert.Equal(ShowSeasonsGetResult.NotGettable, await service.GetSeasonsAsync(_user, series, [9], false, Ct));
        _seasons.Verify(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal(ShowSeasonsGetResult.Requested, await service.GetSeasonsAsync(_user, series, [2], false, Ct));
        _seasons.Verify(s => s.RequestAsync(Show, It.Is<IReadOnlyCollection<int>>(n => n.SequenceEqual(new[] { 2 })), _user.Id, It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(ShowSeasonsGetResult.Requested, await service.GetSeasonsAsync(_user, series, null, false, Ct));
        _seasons.Verify(s => s.RequestAsync(Show, It.Is<IReadOnlyCollection<int>>(n => n.SequenceEqual(new[] { 2, 3 })), _user.Id, It.IsAny<CancellationToken>()), Times.Once);

        // An owned show isn't a discovery show: nothing is saved, no lock taken.
        _lists.Verify(l => l.AddFilmAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DiscoveryMediaType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _sync.Verify(s => s.RunExclusiveAsync(It.IsAny<Func<Task<ShowSeasonsGetResult>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSeasons_DiscoveryShow_SavedUnderTheLock()
    {
        SetTmdb(Tmdb(1, 10), Tmdb(2, 10));
        var series = Series(Show);
        series.SetProviderId(MetadataProvider.Imdb, "tt0096697");
        _sync.Setup(s => s.FindSeries(Show)).Returns(series);

        Assert.Equal(ShowSeasonsGetResult.Requested, await Create().GetSeasonsAsync(_user, series, null, false, Ct));

        _seasons.Verify(s => s.RequestAsync(Show, It.Is<IReadOnlyCollection<int>>(n => n.SequenceEqual(new[] { 1, 2 })), _user.Id, It.IsAny<CancellationToken>()), Times.Once);
        _lists.Verify(l => l.AddFilmAsync(_user.Id, _saved.Id, Show, DiscoveryMediaType.Series, "tt0096697", It.IsAny<CancellationToken>()), Times.Once);
        _sync.Verify(s => s.RunExclusiveAsync(It.IsAny<Func<Task<ShowSeasonsGetResult>>>(), It.IsAny<CancellationToken>()), Times.Once);

        // Gone by the time the lock is held (its list rotated out; found by the page and the check before it): nothing is requested or saved.
        _seasons.Invocations.Clear();
        _lists.Invocations.Clear();
        _sync.SetupSequence(s => s.FindSeries(Show)).Returns(series).Returns(series).Returns((Series?)null);
        Assert.Equal(ShowSeasonsGetResult.NotGettable, await Create().GetSeasonsAsync(_user, series, null, false, Ct));
        _seasons.Verify(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _lists.Verify(l => l.AddFilmAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DiscoveryMediaType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryCopyOfAnOwnedShow_ShowsTheOwnedEpisodes()
    {
        SetTmdb(Tmdb(1, 10), Tmdb(2, 10));
        var copy = Series(Show);
        var owned = Series(Show);
        _sync.Setup(s => s.FindSeries(Show)).Returns(copy);
        _sync.Setup(s => s.FindOwnedSeries(_user, Show, null)).Returns(owned);
        var service = new ShowSeasonsService(
            _tmdb.Object,
            _seasons.Object,
            _requests.Object,
            _lists.Object,
            _sync.Object,
            NullLogger<ShowSeasonsService>.Instance,
            (_, s) => s.Id.Equals(owned.Id) ? Enumerable.Range(1, 10).Select(e => new LibraryEpisode(Guid.NewGuid(), 1, e, null, null, null, null, null, null)).ToList() : [],
            () => _today,
            TimeSpan.FromSeconds(5));

        var page = await service.GetPageAsync(_user, copy, Ct);

        Assert.Equal(new[] { SeasonView.StateOnServer, SeasonView.StateNotOnServer }, page.Seasons.Select(s => s.State));
        Assert.Equal(1, page.GetSeriesSeasonCount);
    }

    [Fact]
    public void ToLibraryEpisode_SkipsMissingAndVirtual()
    {
        var real = new Episode { Id = Guid.NewGuid(), Path = "/tv/Show/Season 1/S01E01-E02.mkv", Name = "Pilot", Overview = "First", ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2, RunTimeTicks = TimeSpan.FromMinutes(44).Ticks, PremiereDate = new DateTime(2020, 3, 4, 0, 0, 0, DateTimeKind.Utc), SeasonName = "Season One" };
        var missing = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 3 };
        var virtualItem = new Episode { Id = Guid.NewGuid(), Path = "/tv/Show/x.mkv", ParentIndexNumber = 1, IndexNumber = 5, IsVirtualItem = true };
        var noSeason = new Episode { Id = Guid.NewGuid(), Path = "/tv/Show/S00.mkv", IndexNumber = 4 };

        var episodes = ShowSeasonsService.ToLibraryEpisodes([real, missing, virtualItem, noSeason, new Season { Id = Guid.NewGuid(), IndexNumber = 1 }]);

        var only = Assert.Single(episodes);
        Assert.Equal(new LibraryEpisode(real.Id, 1, 1, 2, "Pilot", "First", new DateOnly(2020, 3, 4), 44, "Season One"), only);
    }
}
