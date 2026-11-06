using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Limits how many offline downloads the server converts at once, across all devices, so downloads never starve
/// someone watching. Downloads mark their play session with <see cref="SessionPrefix"/>; downloads that only copy the
/// video don't count.
/// </summary>
public static class DownloadConversionLimit
{
    /// <summary>
    /// The play session id prefix that marks an offline download.
    /// </summary>
    public const string SessionPrefix = "download-";

    /// <summary>
    /// How many download conversions may run at once.
    /// </summary>
    public const int MaxConcurrent = 2;

    /// <summary>
    /// Gets whether a play session is an offline download.
    /// </summary>
    /// <param name="playSessionId">The play session id.</param>
    /// <returns>Whether it is a download.</returns>
    public static bool IsDownloadSession(string? playSessionId)
        => playSessionId is not null && playSessionId.StartsWith(SessionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Gets whether another download conversion must wait.
    /// </summary>
    /// <param name="jobs">The active transcoding jobs.</param>
    /// <param name="requestingPlaySessionId">The play session asking to start; its own job doesn't count.</param>
    /// <returns>Whether the limit is reached.</returns>
    public static bool IsAtLimit(IEnumerable<TranscodingJob> jobs, string? requestingPlaySessionId)
        => jobs.Count(job => !job.HasExited
            && !job.IsVideoCopy
            && IsDownloadSession(job.PlaySessionId)
            && !string.Equals(job.PlaySessionId, requestingPlaySessionId, StringComparison.Ordinal)) >= MaxConcurrent;
}
