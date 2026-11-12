namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: what Sonarr is doing with one season of a show, as the discovery app last reported it.
/// </summary>
/// <param name="SeriesTmdbId">The TMDB id of the series.</param>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="Status">The status: <see cref="StatusUnmonitored"/>, <see cref="StatusWanted"/>, <see cref="StatusDownloading"/> or <see cref="StatusIdle"/>.</param>
/// <param name="Mismatch">Whether Sonarr's episode count for the season differs a lot from TMDB's, so its numbering can't be trusted.</param>
public record SonarrSeasonStatus(int SeriesTmdbId, int SeasonNumber, string Status, bool Mismatch)
{
    /// <summary>
    /// The status of a season Sonarr doesn't monitor.
    /// </summary>
    public const string StatusUnmonitored = "unmonitored";

    /// <summary>
    /// The status of a monitored season with episodes missing and nothing queued yet.
    /// </summary>
    public const string StatusWanted = "wanted";

    /// <summary>
    /// The status of a season with queued downloads or a search running.
    /// </summary>
    public const string StatusDownloading = "downloading";

    /// <summary>
    /// The status of a monitored season with nothing queued and the search done.
    /// </summary>
    public const string StatusIdle = "idle";

    /// <summary>
    /// Gets whether a status is one of the four Sonarr season statuses.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>True for a known status.</returns>
    public static bool IsStatus(string? status)
        => status is StatusUnmonitored or StatusWanted or StatusDownloading or StatusIdle;
}
