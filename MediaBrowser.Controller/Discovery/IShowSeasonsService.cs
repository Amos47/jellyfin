using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the show page's seasons, from the library, TMDB, the season requests and Sonarr's season statuses; and the
/// Get season and Get series requests. Nothing is ever added to the library.
/// </summary>
public interface IShowSeasonsService
{
    /// <summary>
    /// Gets every season of a show. Without a TMDB id, or when TMDB fails, only the library's seasons, all on the
    /// server, with nothing to get.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="series">The show, already checked to be visible to the user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<ShowSeasonsPage> GetPageAsync(User user, Series series, CancellationToken ct);

    /// <summary>
    /// Requests seasons of a show: the asked ones that can be got, or every season that can be got when
    /// <paramref name="seasonNumbers"/> is null. A virtual discovery show is also saved to the user's Saved list.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="series">The show, already checked to be visible to the user.</param>
    /// <param name="seasonNumbers">The season numbers, or null for every season that can be got.</param>
    /// <param name="requestDiscoverySeries">
    /// Whether a virtual discovery show also gets today's series download request (<see cref="IDiscoveryRequestStore"/>),
    /// as the Download item action has always made, for older apps.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What came of it.</returns>
    Task<ShowSeasonsGetResult> GetSeasonsAsync(User user, Series series, IReadOnlyCollection<int>? seasonNumbers, bool requestDiscoverySeries, CancellationToken ct);
}
