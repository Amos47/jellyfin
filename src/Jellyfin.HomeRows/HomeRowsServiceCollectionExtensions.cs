using System.Linq;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Providers;
using Jellyfin.HomeRows.Providers.ContinueWatching;
using Jellyfin.HomeRows.Providers.NewReleases;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.HomeRows;

/// <summary>
/// Service registration for home rows.
/// </summary>
public static class HomeRowsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the home row services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddHomeRows(this IServiceCollection services)
    {
        services.AddSingleton<IHomeRowProviderFactory, HomeRowProviderFactory>();
        services.AddSingleton<IHomeRowItemAdapter, HomeRowItemAdapter>();
        services.AddSingleton<IHomeRowConfigurationService, HomeRowConfigurationService>();

        services.AddSingleton<NextUpSource>();
        services.AddSingleton<IHomeRowProvider, NextUpProvider>();
        foreach (var id in new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.ResumeAudio, HomeRowProviderIds.ResumeBook })
        {
            services.AddSingleton<IHomeRowProvider>(sp => ResumeProvider
                .CreateAll(sp.GetRequiredService<ILibraryManager>(), sp.GetRequiredService<ISessionManager>(), sp.GetRequiredService<IHomeRowItemAdapter>())
                .First(p => p.Id == id));
        }

        foreach (var id in new[] { HomeRowProviderIds.LibraryTiles, HomeRowProviderIds.LibraryButtons })
        {
            services.AddSingleton<IHomeRowProvider>(sp => LibraryTilesProvider
                .CreateAll(sp.GetRequiredService<IUserViewManager>(), sp.GetRequiredService<IHomeRowItemAdapter>())
                .First(p => p.Id == id));
        }

        services.AddSingleton<IHomeRowProvider, ContinueWatchingProvider>();
        services.AddSingleton<IHomeRowProvider, LatestMediaProvider>();
        services.AddSingleton<IHomeRowProvider, LiveTvProvider>();
        services.AddSingleton<IHomeRowProvider, ActiveRecordingsProvider>();
        services.AddSingleton<IHomeRowProvider, NewReleasesProvider>();
        services.AddSingleton<IHomeRowProvider, GenreProvider>();
        services.AddSingleton<IHomeRowProvider, MovieDiscoveryProvider>();

        // Later tasks add one line per IHomeRowProvider here.
        return services;
    }
}
