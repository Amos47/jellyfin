using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A film in a user's list, keyed by TMDB id so it stays in the list when it moves from discovery to the library.
/// </summary>
public class CustomUserListItem
{
    /// <summary>
    /// Gets or sets the list id.
    /// </summary>
    public Guid ListId { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id of the film.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the media type of the TMDB id: <c>Movie</c> or <c>Series</c>. Part of the key; rows from before
    /// shows are <c>Movie</c>.
    /// </summary>
    public string MediaType { get; set; } = CustomMediaTypes.Movie;

    /// <summary>
    /// Gets or sets the IMDb id of the film, if known.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the date the film was added (UTC).
    /// </summary>
    public DateTime AddedAt { get; set; }
}
