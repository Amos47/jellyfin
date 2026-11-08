using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Discovery;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: keeps discovery lists, the shown log and the re-pick salts in <see cref="CustomDbContext"/>.
/// </summary>
public sealed class DiscoveryStore : IDiscoveryStore, IDisposable
{
    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The fork db context factory.</param>
    public DiscoveryStore(IDbContextFactory<CustomDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryList>> GetListsAsync(CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entities = await context.DiscoveryLists
                .AsNoTracking()
                .OrderBy(l => l.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(ToList).ToList();
        }
    }

    /// <inheritdoc />
    public async Task<DiscoveryList?> GetListAsync(string id, CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entity = await context.DiscoveryLists
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
                .ConfigureAwait(false);
            return entity is null ? null : ToList(entity);
        }
    }

    /// <inheritdoc />
    public async Task SetListAsync(DiscoveryList list, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(list);

            var filmsJson = JsonSerializer.Serialize(list.Films, JsonDefaults.Options);
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var entity = await context.DiscoveryLists
                    .FirstOrDefaultAsync(l => l.Id == list.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    entity = new CustomDiscoveryList { Id = list.Id, Title = list.Title, Source = list.Source, FilmsJson = filmsJson, Group = list.Group };
                    context.DiscoveryLists.Add(entity);
                }
                else
                {
                    entity.Title = list.Title;
                    entity.Source = list.Source;
                    entity.FilmsJson = filmsJson;
                    entity.Group = list.Group;
                }

                entity.DateModified = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveListAsync(string id, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var removed = await context.DiscoveryLists
                    .Where(l => l.Id == id)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return removed > 0;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkShownAsync(IEnumerable<int> tmdbIds, DateOnly day, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(tmdbIds);

            var ids = tmdbIds.Distinct().ToList();
            if (ids.Count == 0)
            {
                return;
            }

            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var existing = (await context.DiscoveryShown
                    .Where(s => ids.Contains(s.TmdbId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                    .ToDictionary(s => s.TmdbId);

                foreach (var id in ids)
                {
                    if (existing.TryGetValue(id, out var row))
                    {
                        row.LastShown = day;
                    }
                    else
                    {
                        context.DiscoveryShown.Add(new CustomDiscoveryShown { TmdbId = id, FirstShown = day, LastShown = day });
                    }
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, DiscoveryShown>> GetShownAsync(CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.DiscoveryShown.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.ToDictionary(r => r.TmdbId, r => new DiscoveryShown(r.TmdbId, r.FirstShown, r.LastShown));
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetPickSaltAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.DiscoveryPickSalts
                .AsNoTracking()
                .Where(s => s.Day == day)
                .Select(s => s.Salt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SetPickSaltAsync(DateOnly day, string salt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(salt);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var entity = await context.DiscoveryPickSalts
                    .FirstOrDefaultAsync(s => s.Day == day, cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    context.DiscoveryPickSalts.Add(new CustomDiscoveryPickSalt { Day = day, Salt = salt, DateCreated = DateTime.UtcNow });
                }
                else
                {
                    entity.Salt = salt;
                    entity.DateCreated = DateTime.UtcNow;
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // Only the picks of today and later read a salt; a week of slack covers clock and time zone changes.
                var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-7);
                await context.DiscoveryPickSalts
                    .Where(s => s.Day < cutoff && s.Day != day)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static DiscoveryList ToList(CustomDiscoveryList entity)
    {
        var films = JsonSerializer.Deserialize<List<DiscoveryFilmRef>>(entity.FilmsJson, JsonDefaults.Options) ?? [];
        return new DiscoveryList(entity.Id, entity.Title, entity.Source, films, entity.Group);
    }
}
