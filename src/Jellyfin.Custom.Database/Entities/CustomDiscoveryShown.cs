using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// Records when a discovery film was shown on the home screen.
/// </summary>
public class CustomDiscoveryShown
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
    /// Gets or sets the day the film was last shown.
    /// </summary>
    public DateOnly LastShown { get; set; }

    /// <summary>
    /// Gets or sets the day the film was first shown.
    /// </summary>
    public DateOnly FirstShown { get; set; }
}
