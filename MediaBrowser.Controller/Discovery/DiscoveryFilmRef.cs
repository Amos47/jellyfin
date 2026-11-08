namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a reference to a film in a discovery list.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
public record DiscoveryFilmRef(int TmdbId, string? ImdbId);
