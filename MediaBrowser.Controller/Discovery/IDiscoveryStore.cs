using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: stores discovery lists and the log of films shown, in the fork database.
/// </summary>
public interface IDiscoveryStore
{
    /// <summary>
    /// Gets all lists.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lists.</returns>
    Task<IReadOnlyList<DiscoveryList>> GetListsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets one list.
    /// </summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list, or null if there is none.</returns>
    Task<DiscoveryList?> GetListAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a list, or replaces the list with the same id.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetListAsync(DiscoveryList list, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a list.
    /// </summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if a list was removed.</returns>
    Task<bool> RemoveListAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Records that films were shown on a day. The first shown day is kept.
    /// </summary>
    /// <param name="tmdbIds">The TMDB ids.</param>
    /// <param name="day">The day.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task MarkShownAsync(IEnumerable<int> tmdbIds, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the shown log by TMDB id.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The log.</returns>
    Task<IReadOnlyDictionary<int, DiscoveryShown>> GetShownAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the re-pick salt of a day.
    /// </summary>
    /// <param name="day">The server's local date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The salt, or null if the day was never re-picked.</returns>
    Task<string?> GetPickSaltAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the re-pick salt of a day, replacing the one it had. Salts of days more than a week before the server's
    /// local today are pruned.
    /// </summary>
    /// <param name="day">The server's local date.</param>
    /// <param name="salt">The salt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetPickSaltAsync(DateOnly day, string salt, CancellationToken cancellationToken);
}
