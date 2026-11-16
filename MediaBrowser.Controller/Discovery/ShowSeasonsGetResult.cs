namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: what came of a Get season or Get series.
/// </summary>
public enum ShowSeasonsGetResult
{
    /// <summary>
    /// The seasons were requested.
    /// </summary>
    Requested,

    /// <summary>
    /// The show has no TMDB id, so nothing can be requested.
    /// </summary>
    NoTmdbId,

    /// <summary>
    /// No asked season can be got now (on the server, on its way, unaired, specials, or not listed).
    /// </summary>
    NotGettable,
}
