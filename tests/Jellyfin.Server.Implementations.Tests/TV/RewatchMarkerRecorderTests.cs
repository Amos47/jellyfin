using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Emby.Server.Implementations.TV;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.TV;

public sealed class RewatchMarkerRecorderTests : IDisposable
{
    private const string SeriesKey = "svu";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "rewatch-recorder-" + Guid.NewGuid().ToString("N"));
    private readonly User _user = new("user", "auth-provider", "reset-provider") { Id = Guid.NewGuid() };
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly RewatchMarkerStore _store;

    public RewatchMarkerRecorderTests()
    {
        Directory.CreateDirectory(_dataPath);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_dataPath);
        _store = new RewatchMarkerStore(paths.Object, NullLogger<RewatchMarkerStore>.Instance);

        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_user.Id)).Returns(_user);

        var recorder = new RewatchMarkerRecorder(_userData.Object, _library.Object, users.Object, _store, NullLogger<RewatchMarkerRecorder>.Instance);
        recorder.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => Directory.Delete(_dataPath, true);

    private static Episode Episode(int season, int number) => new()
    {
        Id = Guid.NewGuid(),
        ParentIndexNumber = season,
        IndexNumber = number,
        SeriesPresentationUniqueKey = SeriesKey
    };

    private void Frontier(Episode? frontier)
        => _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(frontier is null ? [] : new List<BaseItem> { frontier });

    private void Save(BaseItem item, UserDataSaveReason reason, bool played, long positionTicks, bool? previouslyPlayed = null)
        => _userData.Raise(
            m => m.UserDataSaved += null,
            _userData.Object,
            new UserDataSaveEventArgs
            {
                UserId = _user.Id,
                Item = item,
                SaveReason = reason,
                UserData = new UserItemData { Key = "k", Played = played, PlaybackPositionTicks = positionTicks },
                PreviouslyPlayed = previouslyPlayed
            });

    [Fact]
    public void Progress_OnPlayedEarlierEpisode_SetsMarker()
    {
        var episode = Episode(3, 5);
        Frontier(Episode(27, 7));
        Save(episode, UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks);
        Assert.Equal(episode.Id, _store.Get(_user.Id, SeriesKey)?.EpisodeId);
    }

    [Fact]
    public void Progress_UnderFiveSeconds_NoMarker()
    {
        Frontier(Episode(27, 7));
        Save(Episode(3, 5), UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks - 1);
        Assert.Null(_store.Get(_user.Id, SeriesKey));
    }

    [Fact]
    public void FirstWatchFinished_NoMarker()
    {
        var episode = Episode(10, 3);
        Frontier(Episode(27, 7));
        Save(episode, UserDataSaveReason.PlaybackFinished, true, 0);
        Assert.Null(_store.Get(_user.Id, SeriesKey));
    }

    [Fact]
    public void Replay_OfFrontier_KeepsExistingMarker()
    {
        var rewatch = Episode(3, 5);
        var frontier = Episode(27, 7);
        Frontier(frontier);
        Save(rewatch, UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks);
        Save(frontier, UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks);
        Assert.Equal(rewatch.Id, _store.Get(_user.Id, SeriesKey)?.EpisodeId);
    }

    [Fact]
    public void OfflineUpdate_PreviouslyPlayed_SetsMarker()
    {
        var episode = Episode(3, 5);
        Frontier(Episode(27, 7));
        Save(episode, UserDataSaveReason.UpdateUserData, true, 0, previouslyPlayed: true);
        Assert.Equal(episode.Id, _store.Get(_user.Id, SeriesKey)?.EpisodeId);
    }

    [Fact]
    public void AlternateVersion_RecordsPrimaryId()
    {
        var primaryId = Guid.NewGuid();
        var episode = Episode(3, 5);
        episode.PrimaryVersionId = primaryId;
        Frontier(Episode(27, 7));
        Save(episode, UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks);
        Assert.Equal(primaryId, _store.Get(_user.Id, SeriesKey)?.EpisodeId);
    }

    [Fact]
    public void Movie_Ignored()
    {
        Frontier(Episode(27, 7));
        Save(new Movie { Id = Guid.NewGuid() }, UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks);
        _library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void LibraryThrows_DoesNotPropagate()
    {
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new InvalidOperationException("boom"));
        Save(Episode(3, 5), UserDataSaveReason.PlaybackProgress, true, RewatchRules.MinimumRewatchTicks);
        Assert.Null(_store.Get(_user.Id, SeriesKey));
    }
}
