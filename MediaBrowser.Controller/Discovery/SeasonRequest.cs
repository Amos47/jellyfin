using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a request to get one season of a show onto the server. There is one per series and season, whoever asked;
/// the discovery app carries it out in Sonarr (monitor the season, then search) and reports back.
/// </summary>
/// <param name="SeriesTmdbId">The TMDB id of the series.</param>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="State">The state: <see cref="StatePending"/>, <see cref="StateHandled"/> or <see cref="StateFailed"/>.</param>
/// <param name="Reason">Why the last attempt failed, if it did.</param>
/// <param name="UpdatedAt">When the request last changed (UTC).</param>
public record SeasonRequest(int SeriesTmdbId, int SeasonNumber, string State, string? Reason, DateTime UpdatedAt)
{
    /// <summary>
    /// The state of a request the app hasn't carried out yet.
    /// </summary>
    public const string StatePending = "pending";

    /// <summary>
    /// The state of a request the app has handed to Sonarr.
    /// </summary>
    public const string StateHandled = "handled";

    /// <summary>
    /// The state of a request whose last attempt failed; the season can be requested again.
    /// </summary>
    public const string StateFailed = "failed";

    /// <summary>
    /// Gets whether a state is one of the three request states.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>True for a known state.</returns>
    public static bool IsState(string? state)
        => state is StatePending or StateHandled or StateFailed;
}
