using System;
using System.Collections.Generic;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A request to download a discovery film, one per film whoever asked for it.
/// </summary>
public class CustomDiscoveryRequest
{
    /// <summary>
    /// Gets or sets the TMDB id of the film.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the state: requested, downloading, failed or done.
    /// </summary>
    public required string State { get; set; }

    /// <summary>
    /// Gets or sets the date the request last changed (UTC).
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Gets the users who asked for the film.
    /// </summary>
    public ICollection<CustomDiscoveryRequestUser> Users { get; } = new List<CustomDiscoveryRequestUser>();
}
