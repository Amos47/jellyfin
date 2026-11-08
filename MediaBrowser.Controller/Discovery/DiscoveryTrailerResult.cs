namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the outcome of attaching a trailer file to a discovery film.
/// </summary>
public enum DiscoveryTrailerResult
{
    /// <summary>The trailer is attached.</summary>
    Set,

    /// <summary>There is no discovery film with that TMDB id.</summary>
    FilmNotFound,

    /// <summary>No trailer folder is configured, so no trailers are accepted.</summary>
    NoTrailerRoot,

    /// <summary>The path is not an existing file inside the trailer folder.</summary>
    PathRefused,

    /// <summary>The file is already an item that is not a discovery trailer, such as a library film's extra.</summary>
    PathInUse,
}
