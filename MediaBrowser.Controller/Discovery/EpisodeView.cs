using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one episode on the show page, on the server or not.
/// </summary>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="EpisodeNumber">The episode number, or null for a library episode without one.</param>
/// <param name="Name">The name.</param>
/// <param name="Overview">The overview.</param>
/// <param name="AirDate">The air date.</param>
/// <param name="RuntimeMinutes">The run time in minutes.</param>
/// <param name="StillUrl">The TMDB still image URL, or null.</param>
/// <param name="ItemId">The library item id when the episode is on the server, else null.</param>
/// <param name="State">The state: one of the <c>State…</c> constants.</param>
public record EpisodeView(
    int SeasonNumber,
    int? EpisodeNumber,
    string? Name,
    string? Overview,
    DateOnly? AirDate,
    int? RuntimeMinutes,
    string? StillUrl,
    Guid? ItemId,
    string State)
{
    /// <summary>
    /// The state of an episode in the library.
    /// </summary>
    public const string StateOnServer = "onServer";

    /// <summary>
    /// The state of an aired episode that isn't on the server and isn't on its way.
    /// </summary>
    public const string StateNotOnServer = "notOnServer";

    /// <summary>
    /// The state of an aired episode whose season is requested or downloading.
    /// </summary>
    public const string StateComing = "coming";

    /// <summary>
    /// The state of an episode with no air date yet, or one after today.
    /// </summary>
    public const string StateNotAired = "notAired";
}
