using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: every season of a show, on the server or not, for the show page.
/// </summary>
/// <param name="SeriesTmdbId">The TMDB id of the show, or null when it has none.</param>
/// <param name="CanGetSeries">Whether "Get series" is offered: some season can be got.</param>
/// <param name="GetSeriesSeasonCount">How many seasons "Get series" gets.</param>
/// <param name="Seasons">The seasons, by season number.</param>
/// <param name="HasSeasonRequests">Whether the show has any season request (not sent to clients).</param>
public record ShowSeasonsPage(int? SeriesTmdbId, bool CanGetSeries, int GetSeriesSeasonCount, IReadOnlyList<SeasonView> Seasons, bool HasSeasonRequests = false);
