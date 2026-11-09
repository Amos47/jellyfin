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
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }
}
