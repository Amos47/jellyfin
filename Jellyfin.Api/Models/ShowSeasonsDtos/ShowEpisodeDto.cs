using System;

namespace Jellyfin.Api.Models.ShowSeasonsDtos;

/// <summary>
/// One episode on the show page.
/// </summary>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="EpisodeNumber">The episode number, or null for a library episode without one.</param>
/// <param name="Name">The name.</param>
/// <param name="Overview">The overview.</param>
/// <param name="AirDate">The air date (yyyy-MM-dd), or null.</param>
/// <param name="RuntimeMinutes">The run time in minutes, or null.</param>
/// <param name="StillUrl">The TMDB still image URL, or null.</param>
/// <param name="ItemId">The library item id when the episode is on the server, else null.</param>
/// <param name="State">The state: onServer, notOnServer, coming or notAired.</param>
public sealed record ShowEpisodeDto(
    int SeasonNumber,
    int? EpisodeNumber,
    string? Name,
    string? Overview,
    DateOnly? AirDate,
    int? RuntimeMinutes,
    string? StillUrl,
    Guid? ItemId,
    string State);
