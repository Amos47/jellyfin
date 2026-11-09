using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A user who asked for a discovery film to be downloaded.
/// </summary>
public class CustomDiscoveryRequestUser
{
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
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }
}
