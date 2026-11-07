namespace MediaBrowser.Controller.TV;

/// <summary>
/// Fork: which card Next Up shows for a series.
/// </summary>
public enum NextUpPick
{
    /// <summary>
    /// No card.
    /// </summary>
    None,

    /// <summary>
    /// The next new episode.
    /// </summary>
    NewEpisode,

    /// <summary>
    /// The episode the rewatch marker offers.
    /// </summary>
    Rewatch,

    /// <summary>
    /// The stock rewatching card, when there's no marker.
    /// </summary>
    StockRewatch
}
