using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: stores discovery lists, the log of films shown, the re-pick salts and the saved day picks, in the fork database.
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
    /// Sets the re-pick salt of a day, replacing the one it had, and removes every user's saved picks of that day
    /// (<see cref="GetPickSnapshotAsync"/>) so they are picked afresh. Salts of days more than a week before the
    /// server's local today are pruned.
    /// </summary>
    /// <param name="day">The server's local date.</param>
    /// <param name="salt">The salt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetPickSaltAsync(DateOnly day, string salt, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a user's saved picks of a day.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picked list ids in display order, or null if none were saved.</returns>
    Task<IReadOnlyList<string>?> GetPickSnapshotAsync(Guid userId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a user's picks of a day, unless picks are already saved for that user and day (the first save wins) or
    /// the day's re-pick salt is no longer <paramref name="salt"/> (a re-pick came in while they were worked out).
    /// Saved picks of days more than a week before the server's local today are pruned.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="salt">The re-pick salt the picks were made with, or null for none.</param>
    /// <param name="listIds">The picked list ids, in display order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picks that hold: the saved ones if some were already saved, else <paramref name="listIds"/>.</returns>
    Task<IReadOnlyList<string>> AddPickSnapshotAsync(Guid userId, DateOnly day, string? salt, IReadOnlyList<string> listIds, CancellationToken cancellationToken);
}
