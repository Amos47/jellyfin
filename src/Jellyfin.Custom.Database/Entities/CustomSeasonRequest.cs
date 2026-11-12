using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A request to get one season of a show onto the server, one per series and season whoever asked for it.
/// </summary>
public class CustomSeasonRequest
{
    /// <summary>
    /// Gets or sets the TMDB id of the series.
    /// </summary>
    public int SeriesTmdbId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the state: pending, handled or failed.
    /// </summary>
    public required string State { get; set; }

    /// <summary>
    /// Gets or sets why the last attempt failed, if it did.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the user who first asked for the season.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the date the request last changed (UTC).
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
