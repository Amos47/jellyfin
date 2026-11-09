using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a film in a user's list, keyed by TMDB id.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
/// <param name="AddedAt">When the film was added (UTC).</param>
public record UserListFilm(int TmdbId, string? ImdbId, DateTime AddedAt);
