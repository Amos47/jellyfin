using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The season requests of one series, for the discovery app to carry out in Sonarr.
/// </summary>
/// <param name="SeriesTmdbId">The TMDB id of the series.</param>
/// <param name="SeasonNumbers">The requested season numbers, ascending.</param>
/// <param name="SeriesPath">The folder of the series in the library, or null when no library has it.</param>
/// <param name="Name">The name of the series, from the library or its discovery title, or null when neither has it.</param>
public sealed record DiscoverySeasonRequestDto(int SeriesTmdbId, IReadOnlyList<int> SeasonNumbers, string? SeriesPath, string? Name);
