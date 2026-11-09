namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the kind of title a TMDB id names. TMDB movie and TV ids share one number space, so every place keyed by a
/// TMDB id is keyed by the type too. <see cref="Movie"/> is the default everywhere, so data and callers from before
/// shows keep meaning movies.
/// </summary>
public enum DiscoveryMediaType
{
    /// <summary>
    /// A film (TMDB <c>movie</c>).
    /// </summary>
    Movie = 0,

    /// <summary>
    /// A show (TMDB <c>tv</c>).
    /// </summary>
    Series = 1,
}
