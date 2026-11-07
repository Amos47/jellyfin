using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Server.Implementations.TV;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.TV;

public sealed class RewatchCandidateFinderTests : IDisposable
{
    private const string SeriesKey = "svu";

    private static readonly DateTime MarkerDate = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "rewatch-finder-" + Guid.NewGuid().ToString("N"));
    private readonly User _user = new("user", "auth-provider", "reset-provider") { Id = Guid.NewGuid() };
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly RewatchMarkerStore _store;
    private readonly RewatchCandidateFinder _finder;
    private readonly List<Episode> _episodes;

    public RewatchCandidateFinderTests()
    {
        Directory.CreateDirectory(_dataPath);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_dataPath);
        _store = new RewatchMarkerStore(paths.Object, NullLogger<RewatchMarkerStore>.Instance);

        _episodes = Enumerable.Range(4, 4).Select(n => new Episode
        {
            Id = Guid.NewGuid(),
            ParentIndexNumber = 3,
            IndexNumber = n,
            SeriesPresentationUniqueKey = SeriesKey
        }).ToList();

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => _episodes.Cast<BaseItem>().ToList());

        _finder = new RewatchCandidateFinder(library.Object, _userData.Object, _store, NullLogger<RewatchCandidateFinder>.Instance);
    }

    public void Dispose() => Directory.Delete(_dataPath, true);

    private Episode S3(int number) => _episodes.Single(e => e.IndexNumber == number);

    private void Mark(Episode episode) => _store.Set(_user.Id, new RewatchMarker(SeriesKey, episode.Id, MarkerDate));

    private static Episode Frontier(int season, int number) => new() { ParentIndexNumber = season, IndexNumber = number };

    private (Episode Episode, DateTime Date)? Candidate(BaseItem? frontier)
        => _finder.GetCandidate(_user, SeriesKey, frontier, new DtoOptions());

    [Fact]
    public void NoMarker_Null()
        => Assert.Null(Candidate(Frontier(27, 7)));

    [Fact]
    public void MarkerFinished_ReturnsNextEpisode()
    {
        Mark(S3(5));
        var candidate = Candidate(Frontier(27, 7));
        Assert.Same(S3(6), candidate?.Episode);
        Assert.Equal(MarkerDate, candidate?.Date);
    }

    [Fact]
    public void MarkerResumable_ReturnsMarkerEpisode()
    {
        Mark(S3(5));
        _userData.Setup(u => u.GetUserData(_user, S3(5))).Returns(new UserItemData { Key = "k", Played = true, PlaybackPositionTicks = 600_000_000 });
        Assert.Same(S3(5), Candidate(Frontier(27, 7))?.Episode);
    }

    [Fact]
    public void CandidateAtFrontier_RemovesMarker()
    {
        Mark(S3(5));
        Assert.Null(Candidate(Frontier(3, 6)));
        Assert.Null(_store.Get(_user.Id, SeriesKey));
    }

    [Fact]
    public void MarkerEpisodeMissing_RemovesMarker()
    {
        _store.Set(_user.Id, new RewatchMarker(SeriesKey, Guid.NewGuid(), MarkerDate));
        Assert.Null(Candidate(Frontier(27, 7)));
        Assert.Null(_store.Get(_user.Id, SeriesKey));
    }

    [Fact]
    public void LastEpisode_RemovesMarker()
    {
        Mark(S3(7));
        Assert.Null(Candidate(Frontier(27, 7)));
        Assert.Null(_store.Get(_user.Id, SeriesKey));
    }

    [Fact]
    public void StoreFailure_NoCandidate()
    {
        var store = new Mock<IRewatchMarkerStore>();
        store.Setup(s => s.Get(It.IsAny<Guid>(), It.IsAny<string>())).Returns(new RewatchMarker(SeriesKey, Guid.NewGuid(), MarkerDate));
        store.Setup(s => s.Remove(It.IsAny<Guid>(), It.IsAny<string>())).Throws(new UnauthorizedAccessException("read-only"));
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(_episodes.Cast<BaseItem>().ToList());
        var finder = new RewatchCandidateFinder(library.Object, _userData.Object, store.Object, NullLogger<RewatchCandidateFinder>.Instance);

        Assert.Null(finder.GetCandidate(_user, SeriesKey, Frontier(27, 7), new DtoOptions()));
    }
}
