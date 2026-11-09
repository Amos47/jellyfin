using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Configuration;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: the discovery lists a user's Movie discovery rows (or, by type, Show discovery rows) pick on a day, with each
/// row's <c>maxRows</c> and readiness rules read the way <see cref="DiscoveryRowProvider"/> reads them. Picking itself is
/// left to <see cref="IDiscoveryRowPlanner"/>.
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
    public Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, DateOnly day, CancellationToken cancellationToken)
        => GetPickedListsAsync(user, day, DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, DateOnly day, DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var provider = MovieDiscoverySettings.GetProviderId(type);

        var layout = await _configuration.GetEffectiveLayoutAsync(user, cancellationToken).ConfigureAwait(false);
        var picked = new List<DiscoveryList>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in layout.Rows)
        {
            if (!row.Row.Enabled
                || !row.IsAvailable
                || !string.Equals(row.Row.Provider, provider, StringComparison.Ordinal))
            {
                continue;
            }

            var maxRows = DiscoveryRowProvider.GetMaxRows(row.Row.Config);
            var rules = DiscoveryRowProvider.GetPickRules(row.Row.Config);
            var lists = type == DiscoveryMediaType.Movie
                ? await _planner.GetPickedListsAsync(user, maxRows, rules, day, cancellationToken).ConfigureAwait(false)
                : await _planner.GetPickedListsAsync(user, maxRows, rules, day, type, cancellationToken).ConfigureAwait(false);
            foreach (var list in lists)
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
