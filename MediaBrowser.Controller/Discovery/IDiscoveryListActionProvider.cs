using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: offers one action on a discovery list's page, such as Watch trailers. A list isn't a library item, so these
/// are separate from <see cref="IItemActionProvider"/>; they share its <see cref="ItemAction"/> and
/// <see cref="QueueEntry"/> shapes. Every registered provider is asked for every list.
/// </summary>
public interface IDiscoveryListActionProvider
{
    /// <summary>
    /// Gets the action id, unique among the list action providers.
    /// </summary>
    string ListActionId { get; }

    /// <summary>
    /// Gets the action for a list, if it applies to it for the user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="list">The list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The action, or null if it doesn't apply.</returns>
    Task<ItemAction?> GetListActionAsync(User user, DiscoveryList list, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the entries to play, in order, for a <see cref="ItemAction.PlayQueueKind"/> list action.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="list">The list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<QueueEntry>> GetListQueueEntriesAsync(User user, DiscoveryList list, CancellationToken cancellationToken);
}
