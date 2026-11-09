using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a film in a user's list, keyed by TMDB id and media type.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
/// <param name="AddedAt">When the film was added (UTC).</param>
/// <param name="Type">The media type of the TMDB id: a user list holds films and shows.</param>
public record UserListFilm(int TmdbId, string? ImdbId, DateTime AddedAt, DiscoveryMediaType Type = DiscoveryMediaType.Movie);
