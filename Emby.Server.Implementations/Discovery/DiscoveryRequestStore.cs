using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using MediaBrowser.Controller.Discovery;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: keeps the download requests of discovery films, and the downloads setting, in <see cref="CustomDbContext"/>.
/// </summary>
public sealed class DiscoveryRequestStore : IDiscoveryRequestStore, IDisposable
{
    private const string DownloadsEnabledKey = "downloads-enabled";

    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Read on every title's actions, so kept once read, per type; only this store writes it.
    private readonly ConcurrentDictionary<DiscoveryMediaType, bool> _downloadsEnabled = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryRequestStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The fork db context factory.</param>
    public DiscoveryRequestStore(IDbContextFactory<CustomDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <inheritdoc />
    public Task<DiscoveryRequest?> GetAsync(int tmdbId, CancellationToken cancellationToken)
        => GetAsync(tmdbId, DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<DiscoveryRequest?> GetAsync(int tmdbId, DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        var mediaType = DiscoveryMediaTypes.ToStored(type);
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entity = await context.DiscoveryRequests
                .AsNoTracking()
                .Include(r => r.Users)
                .FirstOrDefaultAsync(r => r.TmdbId == tmdbId && r.MediaType == mediaType, cancellationToken)
                .ConfigureAwait(false);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryRequest>> GetAllAsync(string? state, CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var query = context.DiscoveryRequests.AsNoTracking().Include(r => r.Users).AsQueryable();
            if (state is not null)
            {
                query = query.Where(r => r.State == state);
            }

            var entities = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities
                .OrderByDescending(r => r.UpdatedAt)
                .ThenBy(r => r.TmdbId)
                .ThenBy(r => r.MediaType, StringComparer.Ordinal)
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public Task<DiscoveryRequest> RequestAsync(int tmdbId, Guid userId, CancellationToken cancellationToken)
        => RequestAsync(tmdbId, DiscoveryMediaType.Movie, userId, cancellationToken);

    /// <inheritdoc />
    public async Task<DiscoveryRequest> RequestAsync(int tmdbId, DiscoveryMediaType type, Guid userId, CancellationToken cancellationToken)
    {
        var mediaType = DiscoveryMediaTypes.ToStored(type);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var entity = await context.DiscoveryRequests
                    .Include(r => r.Users)
                    .FirstOrDefaultAsync(r => r.TmdbId == tmdbId && r.MediaType == mediaType, cancellationToken)
                    .ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (entity is null)
                {
                    entity = new CustomDiscoveryRequest { TmdbId = tmdbId, MediaType = mediaType, State = DiscoveryRequest.StateRequested, UpdatedAt = now };
                    context.DiscoveryRequests.Add(entity);
                }
                else if (entity.State is DiscoveryRequest.StateFailed or DiscoveryRequest.StateDone)
                {
                    // Asked for again: the app retries a failed one, and a done film that left the library is fetched again.
                    entity.State = DiscoveryRequest.StateRequested;
                    entity.UpdatedAt = now;
                }

                if (!entity.Users.Any(u => u.UserId.Equals(userId)))
                {
                    entity.Users.Add(new CustomDiscoveryRequestUser { TmdbId = tmdbId, MediaType = mediaType, UserId = userId });
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToModel(entity);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<DiscoveryRequest?> SetStateAsync(int tmdbId, string state, CancellationToken cancellationToken)
        => SetStateAsync(tmdbId, DiscoveryMediaType.Movie, state, cancellationToken);

    /// <inheritdoc />
    public async Task<DiscoveryRequest?> SetStateAsync(int tmdbId, DiscoveryMediaType type, string state, CancellationToken cancellationToken)
    {
        if (!DiscoveryRequest.IsState(state))
        {
            throw new ArgumentException("Unknown request state: " + state, nameof(state));
        }

        var mediaType = DiscoveryMediaTypes.ToStored(type);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var entity = await context.DiscoveryRequests
                    .Include(r => r.Users)
                    .FirstOrDefaultAsync(r => r.TmdbId == tmdbId && r.MediaType == mediaType, cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    return null;
                }

                // Done is final here: a report that raced the done marking must not reopen the request. Only a new
                // request (RequestAsync) does, once the film has left the library.
                if (!string.Equals(entity.State, state, StringComparison.Ordinal)
                    && !string.Equals(entity.State, DiscoveryRequest.StateDone, StringComparison.Ordinal))
                {
                    entity.State = state;
                    entity.UpdatedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                return ToModel(entity);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<int>> GetActiveTmdbIdsAsync(CancellationToken cancellationToken)
        => GetActiveTmdbIdsAsync(DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> GetActiveTmdbIdsAsync(DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        var mediaType = DiscoveryMediaTypes.ToStored(type);
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var ids = await context.DiscoveryRequests
                .AsNoTracking()
                .Where(r => r.MediaType == mediaType && r.State != DiscoveryRequest.StateDone)
                .Select(r => r.TmdbId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return ids.ToHashSet();
        }
    }

    /// <inheritdoc />
    public async Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                await context.DiscoveryRequestUsers
                    .Where(u => u.UserId.Equals(userId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<bool> GetDownloadsEnabledAsync(CancellationToken cancellationToken)
        => GetDownloadsEnabledAsync(DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> GetDownloadsEnabledAsync(DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        if (_downloadsEnabled.TryGetValue(type, out var enabled))
        {
            return enabled;
        }

        var key = GetDownloadsKey(type);
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var value = await context.DiscoverySettings
                .AsNoTracking()
                .Where(s => s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            enabled = bool.TryParse(value, out var parsed) && parsed;
        }

        return _downloadsEnabled.GetOrAdd(type, enabled);
    }

    /// <inheritdoc />
    public Task SetDownloadsEnabledAsync(bool enabled, CancellationToken cancellationToken)
        => SetDownloadsEnabledAsync(DiscoveryMediaType.Movie, enabled, cancellationToken);

    /// <inheritdoc />
    public async Task SetDownloadsEnabledAsync(DiscoveryMediaType type, bool enabled, CancellationToken cancellationToken)
    {
        var key = GetDownloadsKey(type);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var value = enabled.ToString(CultureInfo.InvariantCulture);
                var setting = await context.DiscoverySettings
                    .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
                    .ConfigureAwait(false);
                if (setting is null)
                {
                    context.DiscoverySettings.Add(new CustomDiscoverySetting { Key = key, Value = value });
                }
                else
                {
                    setting.Value = value;
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                _downloadsEnabled[type] = enabled;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // The films keep the key they had before shows.
    private static string GetDownloadsKey(DiscoveryMediaType type)
        => type == DiscoveryMediaType.Movie ? DownloadsEnabledKey : DownloadsEnabledKey + "-" + DiscoveryMediaTypes.ToStored(type).ToLowerInvariant();

    private static DiscoveryRequest ToModel(CustomDiscoveryRequest entity)
        => new(entity.TmdbId, entity.State, entity.UpdatedAt, entity.Users.Select(u => u.UserId).ToArray(), DiscoveryMediaTypes.FromStored(entity.MediaType));
}
