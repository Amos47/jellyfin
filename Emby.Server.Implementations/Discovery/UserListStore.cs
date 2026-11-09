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
/// Fork: keeps the users' named lists in <see cref="CustomDbContext"/>.
/// </summary>
public sealed class UserListStore : IUserListStore, IDisposable
{
    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="UserListStore"/> class.
    /// </summary>
    /// <param name="dbFactory">The fork db context factory.</param>
    public UserListStore(IDbContextFactory<CustomDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserList>> GetListsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var lists = await ReadListsAsync(userId, null, cancellationToken).ConfigureAwait(false);
        if (lists.Any(l => l.IsDefault))
        {
            return lists;
        }

        await EnsureDefaultAsync(userId, cancellationToken).ConfigureAwait(false);
        return await ReadListsAsync(userId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserList> GetDefaultListAsync(Guid userId, CancellationToken cancellationToken)
        => (await GetListsAsync(userId, cancellationToken).ConfigureAwait(false)).First(l => l.IsDefault);

    /// <inheritdoc />
    public async Task<UserList?> GetListAsync(Guid userId, Guid listId, CancellationToken cancellationToken)
        => (await ReadListsAsync(userId, listId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<bool> AddFilmAsync(Guid userId, Guid listId, int tmdbId, string? imdbId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                if (!await OwnsAsync(context, userId, listId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                var existing = await context.UserListItems
                    .FirstOrDefaultAsync(i => i.ListId.Equals(listId) && i.TmdbId == tmdbId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    context.UserListItems.Add(new CustomUserListItem { ListId = listId, TmdbId = tmdbId, ImdbId = imdbId, AddedAt = DateTime.UtcNow });
                }
                else if (existing.ImdbId is null && imdbId is not null)
                {
                    existing.ImdbId = imdbId;
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveFilmAsync(Guid userId, Guid listId, int tmdbId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                if (!await OwnsAsync(context, userId, listId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                await context.UserListItems
                    .Where(i => i.ListId.Equals(listId) && i.TmdbId == tmdbId)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> GetListedTmdbIdsAsync(CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var ids = await context.UserListItems
                .AsNoTracking()
                .Select(i => i.TmdbId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return ids.ToHashSet();
        }
    }

    private static Task<bool> OwnsAsync(CustomDbContext context, Guid userId, Guid listId, CancellationToken cancellationToken)
        => context.UserLists.AnyAsync(l => l.Id.Equals(listId) && l.UserId.Equals(userId), cancellationToken);

    private async Task EnsureDefaultAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                // Under the write lock, so two first reads at once create one list.
                if (await context.UserLists.AnyAsync(l => l.UserId.Equals(userId) && l.IsDefault, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                context.UserLists.Add(new CustomUserList
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = UserList.DefaultName,
                    IsDefault = true,
                    DateCreated = DateTime.UtcNow
                });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // The user's lists (or the one with listId), default first then oldest first, each with its films newest first.
    private async Task<List<UserList>> ReadListsAsync(Guid userId, Guid? listId, CancellationToken cancellationToken)
    {
        var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var query = context.UserLists.AsNoTracking().Include(l => l.Items).Where(l => l.UserId.Equals(userId));
            if (listId is { } id)
            {
                query = query.Where(l => l.Id.Equals(id));
            }

            var entities = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities
                .OrderByDescending(l => l.IsDefault)
                .ThenBy(l => l.DateCreated)
                .Select(l => new UserList(
                    l.Id,
                    l.UserId,
                    l.Name,
                    l.IsDefault,
                    l.Items
                        .OrderByDescending(i => i.AddedAt)
                        .ThenByDescending(i => i.TmdbId)
                        .Select(i => new UserListFilm(i.TmdbId, i.ImdbId, i.AddedAt))
                        .ToArray()))
                .ToList();
        }
    }
}
