using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: offers one action on item pages. Register implementations as <see cref="IItemActionProvider"/>; every
/// registered provider is asked for every item.
/// </summary>
public interface IItemActionProvider
{
    /// <summary>
    /// Gets the action id.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the action for an item, if it applies to it.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item, already checked to be visible to the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The action, or null if it doesn't apply to the item.</returns>
    Task<ItemAction?> GetActionAsync(User user, BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the items to play, in order, for a <see cref="ItemAction.PlayQueueKind"/> action.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item, already checked to be visible to the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item ids.</returns>
    Task<IReadOnlyList<Guid>> GetQueueAsync(User user, BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the entries to play, in order, with the title a player shows for each. The default names nothing, so only
    /// providers that can name their entries override it.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item, already checked to be visible to the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries, in the order of <see cref="GetQueueAsync"/>.</returns>
    async Task<IReadOnlyList<QueueEntry>> GetQueueEntriesAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => (await GetQueueAsync(user, item, cancellationToken).ConfigureAwait(false)).Select(id => new QueueEntry(id, null, null)).ToList();

    /// <summary>
    /// Gets the entries to play, as <see cref="GetQueueEntriesAsync(User, BaseItem, CancellationToken)"/> does, with the
    /// list the user started from (a list page, or a film opened from one). The default ignores the list.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item, already checked to be visible to the user.</param>
    /// <param name="listId">The id of the discovery list the queue was started from, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<QueueEntry>> GetQueueEntriesAsync(User user, BaseItem item, string? listId, CancellationToken cancellationToken)
        => GetQueueEntriesAsync(user, item, cancellationToken);
}
