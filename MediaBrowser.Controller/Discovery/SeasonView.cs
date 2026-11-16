using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one season on the show page, with its state and its episodes.
/// </summary>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="Name">The name.</param>
/// <param name="State">The state: one of the <c>State…</c> constants.</param>
/// <param name="OnServer">How many of its episodes are on the server.</param>
/// <param name="Aired">How many of its episodes have aired (for a season only the library has, its episodes).</param>
/// <param name="Total">How many episodes it has, aired or not, on the server or not.</param>
/// <param name="CanGet">Whether "Get season" is offered.</param>
/// <param name="Episodes">The episodes, by episode number (library episodes without a number last).</param>
/// <param name="RequestFailed">Whether the season's last request failed (not sent to clients).</param>
public record SeasonView(
    int SeasonNumber,
    string Name,
    string State,
    int OnServer,
    int Aired,
    int Total,
    bool CanGet,
    IReadOnlyList<EpisodeView> Episodes,
    bool RequestFailed = false)
{
    /// <summary>
    /// The state of a season with nothing on the server and nothing on its way.
    /// </summary>
    public const string StateNotOnServer = "notOnServer";

    /// <summary>
    /// The state of a season requested, or wanted by Sonarr, and not downloading yet.
    /// </summary>
    public const string StateRequested = "requested";

    /// <summary>
    /// The state of a season Sonarr is downloading or searching.
    /// </summary>
    public const string StateDownloading = "downloading";

    /// <summary>
    /// The state of a season with every aired episode on the server.
    /// </summary>
    public const string StateOnServer = "onServer";

    /// <summary>
    /// The state of a season with some episodes on the server, and nothing on its way.
    /// </summary>
    public const string StatePartly = "partly";
}
