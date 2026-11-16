using System.Collections.Generic;

namespace Jellyfin.Api.Models.ShowSeasonsDtos;

/// <summary>
/// Every season of a show, on the server or not, for the show page.
/// </summary>
/// <param name="SeriesTmdbId">The TMDB id of the show, or null when it has none.</param>
/// <param name="CanGetSeries">Whether "Get series" is offered.</param>
/// <param name="GetSeriesSeasonCount">How many seasons "Get series" gets ("Get all N seasons?").</param>
/// <param name="Seasons">The seasons, by season number.</param>
public sealed record ShowSeasonsDto(int? SeriesTmdbId, bool CanGetSeries, int GetSeriesSeasonCount, IReadOnlyList<ShowSeasonDto> Seasons);
