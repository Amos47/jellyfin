using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding;

public class DownloadConversionLimitTests
{
    private static TranscodingJob Job(string playSessionId, bool isVideoCopy = false, bool hasExited = false)
        => new(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            PlaySessionId = playSessionId,
            IsVideoCopy = isVideoCopy,
            HasExited = hasExited
        };

    [Theory]
    [InlineData("download-abc", true)]
    [InlineData("ABC", false)]
    [InlineData(null, false)]
    public void IsDownloadSession_MatchesPrefix(string? playSessionId, bool expected)
        => Assert.Equal(expected, DownloadConversionLimit.IsDownloadSession(playSessionId));

    [Fact]
    public void IsAtLimit_TwoRunningConversions_Blocks()
        => Assert.True(DownloadConversionLimit.IsAtLimit([Job("download-1"), Job("download-2")], "download-3"));

    [Fact]
    public void IsAtLimit_OneRunningConversion_Allows()
        => Assert.False(DownloadConversionLimit.IsAtLimit([Job("download-1")], "download-3"));

    [Fact]
    public void IsAtLimit_CopiesExitedAndPlaybackDoNotCount()
        => Assert.False(DownloadConversionLimit.IsAtLimit(
            [Job("download-1", isVideoCopy: true), Job("download-2", hasExited: true), Job("playback"), Job("download-4")],
            "download-5"));

    [Fact]
    public void IsAtLimit_OwnSessionDoesNotCount()
        => Assert.False(DownloadConversionLimit.IsAtLimit([Job("download-1"), Job("download-2")], "download-2"));
}
