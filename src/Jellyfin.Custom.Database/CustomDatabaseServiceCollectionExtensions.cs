using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Custom.Database;

/// <summary>
/// Extensions to register and migrate the <see cref="CustomDbContext"/>.
/// </summary>
public static class CustomDatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers a pooled factory for <see cref="CustomDbContext"/> on the same database file as <see cref="JellyfinDbContext"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddCustomDbContext(this IServiceCollection services)
    {
        return services.AddPooledDbContextFactory<CustomDbContext>((serviceProvider, options) =>
        {
            var jellyfinFactory = serviceProvider.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
            string? connectionString;
            using (var jellyfinContext = jellyfinFactory.CreateDbContext())
            {
                connectionString = jellyfinContext.Database.GetConnectionString();
            }

            options.UseSqlite(
                connectionString,
                o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly)
                    .MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName));
        });
    }

    /// <summary>
    /// Applies the pending migrations of <see cref="CustomDbContext"/>.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>A task that completes when the migrations are applied.</returns>
    public static async Task MigrateCustomDatabaseAsync(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<CustomDbContext>>();
        var context = await factory.CreateDbContextAsync().ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
    }
}
