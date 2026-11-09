using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the discovery lists a user's home screen picks on a day, from the Movie discovery rows (films) or the Show
/// discovery rows (shows) of their effective layout. Used by the picks API and the trailer queue so they agree with the
/// rows.
/// </summary>
public interface IDiscoveryUserPicks
{
    /// <summary>
    /// Gets the lists picked for a user's enabled Movie discovery rows on a day, in display order and each once.
    /// A user without such a row gets none.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picked lists.</returns>
    Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the lists of a type picked for a user's enabled discovery rows of that type on a day (Movie discovery rows
    /// for <see cref="DiscoveryMediaType.Movie"/>, as <see cref="GetPickedListsAsync(User, DateOnly, CancellationToken)"/>;
    /// Show discovery rows for <see cref="DiscoveryMediaType.Series"/>), in display order and each once.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="type">The media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picked lists.</returns>
    Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, DateOnly day, DiscoveryMediaType type, CancellationToken cancellationToken);
}
