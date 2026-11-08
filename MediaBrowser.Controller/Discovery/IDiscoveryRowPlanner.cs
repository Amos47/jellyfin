using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: works out which discovery lists a user sees today and which films each shows. The one place that decides
/// this, so the Movie discovery rows, the picks API and the trailer queue always agree.
/// </summary>
public interface IDiscoveryRowPlanner
{
    /// <summary>
    /// Gets the lists picked for a user on a day, in display order. Only lists with at least one film the user may
    /// see are eligible (an owned library film counts as visible); <see cref="DiscoveryPicker"/> picks among them.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="maxRows">The most lists to return.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picked lists.</returns>
    Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the films of a list as the user sees them, in list order: the user's library film when they own it,
    /// else the virtual discovery film if the user may see it. Films with neither are left out.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="list">The list.</param>
    /// <returns>The row items.</returns>
    IReadOnlyList<DiscoveryRowItem> GetRowItems(User user, DiscoveryList list);
}
