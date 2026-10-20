using System.Linq;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Providers;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Library;
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
                .CreateAll(sp.GetRequiredService<ILibraryManager>(), sp.GetRequiredService<IHomeRowItemAdapter>())
                .First(p => p.Id == id));
        }

        // Later tasks add one line per IHomeRowProvider here.
        return services;
    }
}
