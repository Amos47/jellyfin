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
/// Fork: a user's discovery settings, from the enabled Movie discovery rows (films) or Show discovery rows (shows) of
/// their effective layout, read the way <see cref="DiscoveryRowProvider"/> reads them.
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
    public Task<bool> GetExternalTrailersAsync(User user, CancellationToken cancellationToken)
        => GetExternalTrailersAsync(user, DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> GetExternalTrailersAsync(User user, DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var provider = GetProviderId(type);

        var layout = await _configuration.GetEffectiveLayoutAsync(user, cancellationToken).ConfigureAwait(false);
        return layout.Rows.Any(row => row.Row.Enabled
            && row.IsAvailable
            && string.Equals(row.Row.Provider, provider, StringComparison.Ordinal)
            && DiscoveryRowProvider.GetExternalTrailers(row.Row.Config));
    }

    /// <inheritdoc />
    public Task<int> GetReadyThresholdAsync(CancellationToken cancellationToken)
        => GetReadyThresholdAsync(DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<int> GetReadyThresholdAsync(DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        var provider = GetProviderId(type);
        var layouts = new List<IReadOnlyList<HomeRowInstance>>();
        if (await _configuration.GetAdminDefaultAsync(cancellationToken).ConfigureAwait(false) is { } adminDefault)
        {
            layouts.Add(adminDefault);
        }

        layouts.AddRange(await _configuration.GetUserLayoutsAsync(cancellationToken).ConfigureAwait(false));
        return layouts
            .SelectMany(rows => rows)
            .Where(row => row.Enabled && string.Equals(row.Provider, provider, StringComparison.Ordinal))
            .Select(row => DiscoveryRowProvider.GetPickRules(row.Config).MinReadyFilms)
            .DefaultIfEmpty(DiscoveryPickRules.DefaultMinReadyFilms)
            .Max();
    }

    /// <summary>
    /// Gets the id of the discovery row provider of a media type: Movie discovery for films, Show discovery for shows.
    /// </summary>
    /// <param name="type">The media type.</param>
    /// <returns>The provider id.</returns>
    internal static string GetProviderId(DiscoveryMediaType type)
        => type == DiscoveryMediaType.Series ? HomeRowProviderIds.ShowDiscovery : HomeRowProviderIds.MovieDiscovery;
}
