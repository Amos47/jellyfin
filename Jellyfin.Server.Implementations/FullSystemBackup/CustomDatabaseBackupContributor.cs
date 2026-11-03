using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Backs up and restores the fork's own tables of <see cref="CustomDbContext"/>.
/// </summary>
public class CustomDatabaseBackupContributor : IBackupContributor
{
    private const string ConfigurationsEntryName = "Custom/CustomHomeRowConfigurations.json";
    private const string DefaultsEntryName = "Custom/CustomHomeRowDefaults.json";

    private readonly IDbContextFactory<CustomDbContext> _customDbProvider;
    private readonly IDbContextFactory<JellyfinDbContext> _jellyfinDbProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomDatabaseBackupContributor"/> class.
    /// </summary>
    /// <param name="customDbProvider">The fork database factory.</param>
    /// <param name="jellyfinDbProvider">The Jellyfin database factory, used to look up users.</param>
    public CustomDatabaseBackupContributor(
        IDbContextFactory<CustomDbContext> customDbProvider,
        IDbContextFactory<JellyfinDbContext> jellyfinDbProvider)
    {
        _customDbProvider = customDbProvider;
        _jellyfinDbProvider = jellyfinDbProvider;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var context = await _customDbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var configurations = await context.HomeRowConfigurations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            await WriteEntryAsync(archive, ConfigurationsEntryName, configurations, cancellationToken).ConfigureAwait(false);

            var defaults = await context.HomeRowDefaults.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            await WriteEntryAsync(archive, DefaultsEntryName, defaults, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RestoreAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var context = await _customDbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // A restore runs before the regular startup migration, so the fork tables may not exist yet.
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var configurations = await ReadEntryAsync<CustomHomeRowConfiguration>(archive, ConfigurationsEntryName, cancellationToken).ConfigureAwait(false);
            if (configurations is not null)
            {
                context.HomeRowConfigurations.RemoveRange(await context.HomeRowConfigurations.ToListAsync(cancellationToken).ConfigureAwait(false));
                context.HomeRowConfigurations.AddRange(configurations);
            }
            else
            {
                // Backup without fork data: keep the layouts of users that still exist, drop the others.
                var jellyfinContext = await _jellyfinDbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HashSet<Guid> userIds;
                await using (jellyfinContext.ConfigureAwait(false))
                {
                    userIds = (await jellyfinContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
                }

                var existing = await context.HomeRowConfigurations.ToListAsync(cancellationToken).ConfigureAwait(false);
                context.HomeRowConfigurations.RemoveRange(existing.Where(c => !userIds.Contains(c.UserId)));
            }

            var defaults = await ReadEntryAsync<CustomHomeRowDefault>(archive, DefaultsEntryName, cancellationToken).ConfigureAwait(false);
            if (defaults is not null)
            {
                context.HomeRowDefaults.RemoveRange(await context.HomeRowDefaults.ToListAsync(cancellationToken).ConfigureAwait(false));
                context.HomeRowDefaults.AddRange(defaults);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteEntryAsync<T>(ZipArchive archive, string name, List<T> values, CancellationToken cancellationToken)
    {
        var stream = await archive.CreateEntry(name).OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, values, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<T>?> ReadEntryAsync<T>(ZipArchive archive, string name, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
