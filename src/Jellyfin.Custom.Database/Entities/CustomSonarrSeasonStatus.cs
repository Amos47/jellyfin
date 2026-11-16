using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// What Sonarr is doing with one season of a show, as the discovery app last reported it.
/// </summary>
public class CustomSonarrSeasonStatus
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
    /// Gets or sets the status: unmonitored, wanted, downloading or idle.
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Sonarr's episode count for the season differs a lot from TMDB's.
    /// </summary>
    public bool Mismatch { get; set; }

    /// <summary>
    /// Gets or sets the date the app last reported the status (UTC).
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
