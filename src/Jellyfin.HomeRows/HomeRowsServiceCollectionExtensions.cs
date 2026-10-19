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

        // Later tasks add the configuration service and one line per IHomeRowProvider here.
        return services;
    }
}
