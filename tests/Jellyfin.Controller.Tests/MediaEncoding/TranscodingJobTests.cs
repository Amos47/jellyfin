using System;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding;

public class TranscodingJobTests
{
    [Fact]
    public void SegmentRequest_HlsJob_CountsAsPing()
    {
        // A downloader asks for segments back to back and never sends playback pings.
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            LastPingDate = DateTime.UtcNow.AddMinutes(-5)
        };
        var now = DateTime.UtcNow;

        job.OnSegmentRequested(now);

        Assert.Equal(now, job.LastPingDate);
    }

    [Fact]
    public void SegmentRequest_ProgressiveJob_LeavesPingAlone()
    {
        var earlier = DateTime.UtcNow.AddMinutes(-5);
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Progressive,
            LastPingDate = earlier
        };

        job.OnSegmentRequested(DateTime.UtcNow);

        Assert.Equal(earlier, job.LastPingDate);
    }
}
