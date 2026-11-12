using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

public class DownloadActionTests
{
    private readonly User _user = new("tester", "Default", "Default");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Perform_FilmGoneBeforeTheLock_PerformsNothing()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        film.SetProviderId(MetadataProvider.Tmdb, 77.ToString(CultureInfo.InvariantCulture));

        var requests = new Mock<IDiscoveryRequestStore>();
        var lists = new Mock<IUserListStore>();
        var sync = new Mock<IDiscoveryFilmSync>();

        // Found when the action is resolved, gone once the lock is held (a sync deleted it in between).
        sync.SetupSequence(s => s.FindFilm(77)).Returns(film).Returns((Movie?)null);
        sync.Setup(s => s.RunExclusiveAsync(It.IsAny<Func<Task<DiscoveryRequest?>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task<DiscoveryRequest?>> work, CancellationToken _) => work());

        var action = new DownloadAction(requests.Object, lists.Object, sync.Object, Mock.Of<IShowSeasonsService>());

        Assert.Null(await action.PerformAsync(_user, film, Ct));
        requests.Verify(r => r.RequestAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        lists.Verify(l => l.AddFilmAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadAction_Series_LabelGetSeries_AndWritesSeasonRequests()
    {
        var shows = new ShowFixture();
        var show = shows.Series(1100);
        shows.Sync.Setup(s => s.FindSeries(1100)).Returns(show);
        shows.Tmdb(1100, 3);

        var action = await shows.Action.GetActionAsync(_user, show, Ct);

        Assert.NotNull(action);
        Assert.Equal((DownloadAction.ActionId, "Get series", "download", ItemAction.RequestKind), (action.Id, action.Label, action.Icon, action.Kind));
        Assert.Equal((DownloadAction.StateAvailable, true), (action.State, action.Enabled));
        Assert.Equal(
            new Dictionary<string, string>
            {
                [DownloadAction.StateAvailable] = "Get series",
                [DownloadAction.StateFailed] = "Retry",
                [DownloadAction.StateRequested] = "Requested",
                [DownloadAction.StateDownloading] = "Getting…",
            },
            action.Labels);

        // Get series: every season it can get, the series request older apps know, and Saved.
        var performed = await shows.Action.PerformAsync(_user, show, Ct);

        shows.Seasons.Verify(s => s.RequestAsync(1100, It.Is<IReadOnlyCollection<int>>(n => n.SequenceEqual(new[] { 1, 2, 3 })), _user.Id, It.IsAny<CancellationToken>()), Times.Once);
        shows.Requests.Verify(r => r.RequestAsync(1100, DiscoveryMediaType.Series, _user.Id, It.IsAny<CancellationToken>()), Times.Once);
        shows.Lists.Verify(l => l.AddFilmAsync(_user.Id, shows.Saved.Id, 1100, DiscoveryMediaType.Series, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal((DownloadAction.StateRequested, "Requested", false), (performed!.State, performed.Label, performed.Enabled));

        // Requested: posting again requests nothing more.
        await shows.Action.PerformAsync(_user, show, Ct);
        shows.Seasons.Verify(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

        // Downloading once Sonarr has it; Retry when a season's request failed.
        shows.SetRequests(1100);
        shows.SetSonarr(1100, new SonarrSeasonStatus(1100, 2, SonarrSeasonStatus.StatusDownloading, false));
        var downloading = await shows.Action.GetActionAsync(_user, show, Ct);
        Assert.Equal((DownloadAction.StateDownloading, "Getting…", false), (downloading!.State, downloading.Label, downloading.Enabled));

        shows.SetSonarr(1100);
        shows.SetRequests(1100, new SeasonRequest(1100, 2, SeasonRequest.StateFailed, "no release", DateTime.UtcNow));
        var failed = await shows.Action.GetActionAsync(_user, show, Ct);
        Assert.Equal((DownloadAction.StateFailed, "Retry", true), (failed!.State, failed.Label, failed.Enabled));
    }

    [Fact]
    public async Task DownloadAction_Series_DownloadsOff_Hidden()
    {
        var shows = new ShowFixture();
        var show = shows.Series(1150);
        shows.Sync.Setup(s => s.FindSeries(1150)).Returns(show);
        shows.Tmdb(1150, 3);
        shows.Requests.Setup(r => r.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.Null(await shows.Action.GetActionAsync(_user, show, Ct));
        Assert.Null(await shows.Action.PerformAsync(_user, show, Ct));
        shows.Seasons.Verify(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadAction_OwnedPartialSeries_Offered()
    {
        var shows = new ShowFixture();
        var show = shows.Series(1200);
        shows.Tmdb(1200, 3);
        shows.AddLibrary(1, 10);

        var action = await shows.Action.GetActionAsync(_user, show, Ct);
        Assert.Equal(("Get series", DownloadAction.StateAvailable, true), (action!.Label, action.State, action.Enabled));

        // Only the seasons it doesn't have; an owned show gets no series request and isn't saved.
        var performed = await shows.Action.PerformAsync(_user, show, Ct);
        shows.Seasons.Verify(s => s.RequestAsync(1200, It.Is<IReadOnlyCollection<int>>(n => n.SequenceEqual(new[] { 2, 3 })), _user.Id, It.IsAny<CancellationToken>()), Times.Once);
        shows.Requests.Verify(r => r.RequestAsync(It.IsAny<int>(), It.IsAny<DiscoveryMediaType>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        shows.Lists.Verify(l => l.AddFilmAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DiscoveryMediaType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DownloadAction.StateRequested, performed!.State);

        // Everything requested: the action stays, disabled, rather than vanishing.
        var requested = await shows.Action.GetActionAsync(_user, show, Ct);
        Assert.Equal(("Requested", false), (requested!.Label, requested.Enabled));
    }

    [Fact]
    public async Task DownloadAction_FullyOwned_Hidden()
    {
        var shows = new ShowFixture();
        var show = shows.Series(1300);
        shows.Tmdb(1300, 2);
        shows.AddLibrary(1, 10);
        shows.AddLibrary(2, 10);

        Assert.Null(await shows.Action.GetActionAsync(_user, show, Ct));
        Assert.Null(await shows.Action.PerformAsync(_user, show, Ct));

        // No TMDB id: nothing to get either.
        var noId = new Series { Id = Guid.NewGuid() };
        Assert.Null(await shows.Action.GetActionAsync(_user, noId, Ct));
        shows.Seasons.Verify(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A real show page service over mocked stores, a fake TMDB and a fake library, with show downloads on.
    private sealed class ShowFixture
    {
        private readonly Dictionary<int, IReadOnlyList<TmdbSeason>> _tmdb = [];
        private readonly Dictionary<int, List<SeasonRequest>> _requests = [];
        private readonly Dictionary<int, IReadOnlyList<SonarrSeasonStatus>> _sonarr = [];
        private readonly List<LibraryEpisode> _library = [];

        public ShowFixture()
        {
            var tmdb = new Mock<ITmdbSeasonSource>();
            tmdb.Setup(t => t.GetSeasonsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, string? _, string? _, CancellationToken _) => _tmdb.GetValueOrDefault(id));
            Seasons.Setup(s => s.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, CancellationToken _) => (IReadOnlyList<SeasonRequest>)_requests.GetValueOrDefault(id, []).ToList());
            Seasons.Setup(s => s.GetSonarrStatusAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, CancellationToken _) => _sonarr.GetValueOrDefault(id, []));
            Seasons.Setup(s => s.RequestAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback((int id, IReadOnlyCollection<int> seasons, Guid _, CancellationToken _) =>
                {
                    var list = _requests.TryGetValue(id, out var existing) ? existing : _requests[id] = [];
                    list.RemoveAll(r => seasons.Contains(r.SeasonNumber));
                    list.AddRange(seasons.Select(n => new SeasonRequest(id, n, SeasonRequest.StatePending, null, DateTime.UtcNow)));
                })
                .Returns(Task.CompletedTask);
            Requests.Setup(r => r.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Requests.Setup(r => r.RequestAsync(It.IsAny<int>(), DiscoveryMediaType.Series, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, DiscoveryMediaType type, Guid _, CancellationToken _) => new DiscoveryRequest(id, DiscoveryRequest.StateRequested, DateTime.UtcNow, [], type));
            Lists.Setup(l => l.GetDefaultListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Saved);
            Sync.Setup(s => s.RunExclusiveAsync(It.IsAny<Func<Task<ShowSeasonsGetResult>>>(), It.IsAny<CancellationToken>()))
                .Returns((Func<Task<ShowSeasonsGetResult>> work, CancellationToken _) => work());

            var service = new ShowSeasonsService(
                tmdb.Object,
                Seasons.Object,
                Requests.Object,
                Lists.Object,
                Sync.Object,
                NullLogger<ShowSeasonsService>.Instance,
                (_, _) => _library,
                () => new DateOnly(2026, 11, 11),
                TimeSpan.FromSeconds(5));
            Action = new DownloadAction(Requests.Object, Lists.Object, Sync.Object, service);
        }

        public Mock<ISeasonStore> Seasons { get; } = new();

        public Mock<IDiscoveryRequestStore> Requests { get; } = new();

        public Mock<IUserListStore> Lists { get; } = new();

        public Mock<IDiscoveryFilmSync> Sync { get; } = new();

        public UserList Saved { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "Saved", true, []);

        public DownloadAction Action { get; }

        public Series Series(int tmdbId)
        {
            var series = new Series { Id = Guid.NewGuid(), Name = "Show " + tmdbId.ToString(CultureInfo.InvariantCulture) };
            series.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));
            return series;
        }

        public void Tmdb(int tmdbId, int seasons)
            => _tmdb[tmdbId] = Enumerable.Range(1, seasons)
                .Select(n => new TmdbSeason(n, "Season " + n, Enumerable.Range(1, 10).Select(e => new TmdbEpisode(e, "E" + e, null, new DateOnly(2020, 1, 1), 22, null)).ToList()))
                .ToList();

        public void AddLibrary(int season, int episodes)
            => _library.AddRange(Enumerable.Range(1, episodes).Select(e => new LibraryEpisode(Guid.NewGuid(), season, e, null, "E" + e, null, null, 22, null)));

        public void SetRequests(int tmdbId, params SeasonRequest[] requests) => _requests[tmdbId] = requests.ToList();

        public void SetSonarr(int tmdbId, params SonarrSeasonStatus[] statuses) => _sonarr[tmdbId] = statuses;
    }
}
