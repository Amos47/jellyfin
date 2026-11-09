using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Configuration;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: the discovery lists a user's Movie discovery rows pick on a day, with each row's <c>maxRows</c> and readiness
/// rules read the way <see cref="MovieDiscoveryProvider"/> reads them. Picking itself is left to <see cref="IDiscoveryRowPlanner"/>.
/// </summary>
public sealed class MovieDiscoveryPicks : IDiscoveryUserPicks
{
    private readonly IHomeRowConfigurationService _configuration;
    private readonly IDiscoveryRowPlanner _planner;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieDiscoveryPicks"/> class.
    /// </summary>
    /// <param name="configuration">The home row configuration service.</param>
    /// <param name="planner">The discovery row planner.</param>
    public MovieDiscoveryPicks(IHomeRowConfigurationService configuration, IDiscoveryRowPlanner planner)
    {
        _configuration = configuration;
        _planner = planner;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var layout = await _configuration.GetEffectiveLayoutAsync(user, cancellationToken).ConfigureAwait(false);
        var picked = new List<DiscoveryList>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in layout.Rows)
        {
            if (!row.Row.Enabled
                || !row.IsAvailable
                || !string.Equals(row.Row.Provider, HomeRowProviderIds.MovieDiscovery, StringComparison.Ordinal))
            {
                continue;
            }

            var maxRows = MovieDiscoveryProvider.GetMaxRows(row.Row.Config);
            foreach (var list in await _planner.GetPickedListsAsync(user, maxRows, MovieDiscoveryProvider.GetPickRules(row.Row.Config), day, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(list.Id))
                {
                    picked.Add(list);
                }
            }
        }

        return picked;
    }
}
