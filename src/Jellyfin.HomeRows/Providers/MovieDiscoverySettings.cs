using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Configuration;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: a user's discovery settings, from the enabled Movie discovery rows of their effective layout, read the way
/// <see cref="MovieDiscoveryProvider"/> reads them.
/// </summary>
public sealed class MovieDiscoverySettings : IDiscoveryUserSettings
{
    private readonly IHomeRowConfigurationService _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieDiscoverySettings"/> class.
    /// </summary>
    /// <param name="configuration">The home row configuration service.</param>
    public MovieDiscoverySettings(IHomeRowConfigurationService configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<bool> GetExternalTrailersAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var layout = await _configuration.GetEffectiveLayoutAsync(user, cancellationToken).ConfigureAwait(false);
        return layout.Rows.Any(row => row.Row.Enabled
            && row.IsAvailable
            && string.Equals(row.Row.Provider, HomeRowProviderIds.MovieDiscovery, StringComparison.Ordinal)
            && MovieDiscoveryProvider.GetExternalTrailers(row.Row.Config));
    }

    /// <inheritdoc />
    public async Task<int> GetReadyThresholdAsync(CancellationToken cancellationToken)
    {
        var layouts = new List<IReadOnlyList<HomeRowInstance>>();
        if (await _configuration.GetAdminDefaultAsync(cancellationToken).ConfigureAwait(false) is { } adminDefault)
        {
            layouts.Add(adminDefault);
        }

        layouts.AddRange(await _configuration.GetUserLayoutsAsync(cancellationToken).ConfigureAwait(false));
        return layouts
            .SelectMany(rows => rows)
            .Where(row => row.Enabled && string.Equals(row.Provider, HomeRowProviderIds.MovieDiscovery, StringComparison.Ordinal))
            .Select(row => MovieDiscoveryProvider.GetPickRules(row.Config).MinReadyFilms)
            .DefaultIfEmpty(DiscoveryPickRules.DefaultMinReadyFilms)
            .Max();
    }
}
