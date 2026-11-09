namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a reference to a film in a discovery list.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
/// <param name="Type">The media type of the TMDB id. In a discovery list it is always the list's
/// <see cref="DiscoveryList.ItemType"/>; in a user list it is each film's own.</param>
public record DiscoveryFilmRef(int TmdbId, string? ImdbId, DiscoveryMediaType Type = DiscoveryMediaType.Movie);
