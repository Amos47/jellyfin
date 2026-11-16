using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using MediaBrowser.Controller.Discovery;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: keeps the season requests of shows, and the Sonarr season statuses, in <see cref="CustomDbContext"/>.
/// </summary>
public sealed class SeasonStore : ISeasonStore, IDisposable
{
    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The fork db context factory.</param>
    public SeasonStore(IDbContextFactory<CustomDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <inheritdoc />
    public async Task RequestAsync(int seriesTmdbId, IReadOnlyCollection<int> seasons, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        var wanted = seasons.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var existing = await context.SeasonRequests
                    .Where(r => r.SeriesTmdbId == seriesTmdbId && wanted.Contains(r.SeasonNumber))
                    .ToDictionaryAsync(r => r.SeasonNumber, ct)
                    .ConfigureAwait(false);
                var now = DateTime.UtcNow;
                foreach (var season in wanted)
                {
                    if (!existing.TryGetValue(season, out var entity))
                    {
                        context.SeasonRequests.Add(new CustomSeasonRequest
                        {
                            SeriesTmdbId = seriesTmdbId,
                            SeasonNumber = season,
                            State = SeasonRequest.StatePending,
                            UserId = userId,
                            UpdatedAt = now,
                        });
                    }
                    else if (!string.Equals(entity.State, SeasonRequest.StatePending, StringComparison.Ordinal))
                    {
                        // Asked for again: the app retries a failed one, and a handled one that never arrived is searched
                        // again. A pending one is left alone, so pressing Get twice never searches twice.
                        entity.State = SeasonRequest.StatePending;
                        entity.Reason = null;
                        entity.UpdatedAt = now;
                    }
                }

                await context.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeasonRequest>> GetAsync(int seriesTmdbId, CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entities = await context.SeasonRequests
                .AsNoTracking()
                .Where(r => r.SeriesTmdbId == seriesTmdbId)
                .OrderBy(r => r.SeasonNumber)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return entities.Select(ToModel).ToList();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeasonRequest>> GetPendingAsync(CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entities = await context.SeasonRequests
                .AsNoTracking()
                .Where(r => r.State == SeasonRequest.StatePending)
                .OrderBy(r => r.SeriesTmdbId)
                .ThenBy(r => r.SeasonNumber)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return entities.Select(ToModel).ToList();
        }
    }

    /// <inheritdoc />
    public async Task SetStateAsync(int seriesTmdbId, int season, string state, string? reason, CancellationToken ct)
    {
        if (!SeasonRequest.IsState(state))
        {
            throw new ArgumentException("Unknown season request state: " + state, nameof(state));
        }

        // A reason only explains a failure, and is kept short.
        var kept = string.Equals(state, SeasonRequest.StateFailed, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(reason)
            ? reason.Trim()
            : null;
        if (kept?.Length > CustomDbContext.SeasonRequestReasonMaxLength)
        {
            kept = kept[..CustomDbContext.SeasonRequestReasonMaxLength];
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var entity = await context.SeasonRequests
                    .FirstOrDefaultAsync(r => r.SeriesTmdbId == seriesTmdbId && r.SeasonNumber == season, ct)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    return;
                }

                if (!string.Equals(entity.State, state, StringComparison.Ordinal) || !string.Equals(entity.Reason, kept, StringComparison.Ordinal))
                {
                    entity.State = state;
                    entity.Reason = kept;
                    entity.UpdatedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReplaceSonarrStatusAsync(IReadOnlyList<SonarrSeasonStatus> all, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (all.FirstOrDefault(s => !SonarrSeasonStatus.IsStatus(s.Status)) is { } unknown)
        {
            throw new ArgumentException("Unknown Sonarr season status: " + unknown.Status, nameof(all));
        }

        // A season listed twice keeps its last status: the key allows one row each.
        var latest = new Dictionary<(int, int), SonarrSeasonStatus>();
        foreach (var status in all)
        {
            latest[(status.SeriesTmdbId, status.SeasonNumber)] = status;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await context.SonarrSeasonStatuses.ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    context.SonarrSeasonStatuses.AddRange(latest.Values.Select(s => new CustomSonarrSeasonStatus
                    {
                        SeriesTmdbId = s.SeriesTmdbId,
                        SeasonNumber = s.SeasonNumber,
                        Status = s.Status,
                        Mismatch = s.Mismatch,
                        UpdatedAt = now,
                    }));
                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SonarrSeasonStatus>> GetSonarrStatusAsync(int seriesTmdbId, CancellationToken ct)
    {
        var context = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entities = await context.SonarrSeasonStatuses
                .AsNoTracking()
                .Where(s => s.SeriesTmdbId == seriesTmdbId)
                .OrderBy(s => s.SeasonNumber)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return entities.Select(s => new SonarrSeasonStatus(s.SeriesTmdbId, s.SeasonNumber, s.Status, s.Mismatch)).ToList();
        }
    }

    private static SeasonRequest ToModel(CustomSeasonRequest entity)
        => new(entity.SeriesTmdbId, entity.SeasonNumber, entity.State, entity.Reason, entity.UpdatedAt);
}
