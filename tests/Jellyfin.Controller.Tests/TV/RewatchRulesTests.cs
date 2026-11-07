using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Controller.Tests.TV;

public class RewatchRulesTests
{
    private const long FiveSeconds = RewatchRules.MinimumRewatchTicks;

    private static readonly EpisodePosition S3E5 = new(3, 5);
    private static readonly EpisodePosition S27E7 = new(27, 7);

    [Theory]
    [InlineData(UserDataSaveReason.PlaybackProgress, true, null, FiveSeconds, false, true)]
    [InlineData(UserDataSaveReason.PlaybackFinished, true, null, FiveSeconds, false, true)]
    [InlineData(UserDataSaveReason.PlaybackProgress, true, null, FiveSeconds - 1, false, false)]
    [InlineData(UserDataSaveReason.PlaybackFinished, true, null, 0L, true, true)]
    [InlineData(UserDataSaveReason.PlaybackProgress, false, null, FiveSeconds, false, false)]
    [InlineData(UserDataSaveReason.PlaybackFinished, false, null, FiveSeconds, true, false)]
    [InlineData(UserDataSaveReason.UpdateUserData, false, true, FiveSeconds, false, true)]
    [InlineData(UserDataSaveReason.UpdateUserData, true, true, 0L, false, true)]
    [InlineData(UserDataSaveReason.UpdateUserData, true, false, FiveSeconds, false, false)]
    [InlineData(UserDataSaveReason.UpdateUserData, true, null, FiveSeconds, false, false)]
    [InlineData(UserDataSaveReason.TogglePlayed, true, true, FiveSeconds, true, false)]
    [InlineData(UserDataSaveReason.PlaybackStart, true, null, FiveSeconds, true, false)]
    public void ShouldRecord_FollowsTable(UserDataSaveReason reason, bool played, bool? previouslyPlayed, long position, bool markerIsThisEpisode, bool expected)
        => Assert.Equal(expected, RewatchRules.ShouldRecord(reason, played, previouslyPlayed, position, markerIsThisEpisode));

    [Fact]
    public void ShouldRecord_FirstWatchFinished_False()
        => Assert.False(RewatchRules.ShouldRecord(UserDataSaveReason.PlaybackFinished, true, null, 0, false));

    [Fact]
    public void IsRewatchPlay_BeforeFrontier_True()
        => Assert.True(RewatchRules.IsRewatchPlay(S3E5, S27E7));

    [Fact]
    public void IsRewatchPlay_AtFrontier_False()
        => Assert.False(RewatchRules.IsRewatchPlay(S27E7, S27E7));

    [Fact]
    public void IsRewatchPlay_NoFrontier_False()
        => Assert.False(RewatchRules.IsRewatchPlay(S3E5, null));

    [Fact]
    public void IsCaughtUp_AtFrontier_True()
        => Assert.True(RewatchRules.IsCaughtUp(S27E7, S27E7));

    [Fact]
    public void IsCaughtUp_Null_True()
        => Assert.True(RewatchRules.IsCaughtUp(null, S27E7));

    [Fact]
    public void IsCaughtUp_Before_False()
        => Assert.False(RewatchRules.IsCaughtUp(S3E5, S27E7));

    [Fact]
    public void EpisodePosition_OrdersBySeasonThenEpisode()
    {
        Assert.True(new EpisodePosition(2, 10) < new EpisodePosition(3, 1));
        Assert.True(new EpisodePosition(3, 2) > new EpisodePosition(3, 1));
        Assert.True(new EpisodePosition(3, 1) >= new EpisodePosition(3, 1));
    }
}
