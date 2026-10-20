using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// Reads and saves home row layouts. A user without a saved layout inherits, live, the admin default, else their
/// upstream home sections, else upstream's standard order.
/// </summary>
public interface IHomeRowConfigurationService
{
    /// <summary>
    /// Gets the layout that applies to a user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The effective layout.</returns>
    Task<EffectiveHomeRowLayout> GetEffectiveLayoutAsync(User user, CancellationToken ct);

    /// <summary>
    /// Validates and saves a user's own layout.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="rows">The rows, in display order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The errors; empty when the layout was saved.</returns>
    Task<IReadOnlyList<HomeRowSaveError>> SaveUserLayoutAsync(User user, IReadOnlyList<HomeRowInstance> rows, CancellationToken ct);

    /// <summary>
    /// Deletes a user's own layout, so that they inherit the default again. Does nothing if there is none.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the layout is deleted.</returns>
    Task ResetUserLayoutAsync(User user, CancellationToken ct);

    /// <summary>
    /// Gets the admin default layout.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The rows, or <c>null</c> if no admin has saved a default.</returns>
    Task<IReadOnlyList<HomeRowInstance>?> GetAdminDefaultAsync(CancellationToken ct);

    /// <summary>
    /// Validates and saves the admin default layout.
    /// </summary>
    /// <param name="admin">The admin saving the default, whose schema context is used.</param>
    /// <param name="rows">The rows, in display order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The errors; empty when the default was saved.</returns>
    Task<IReadOnlyList<HomeRowSaveError>> SaveAdminDefaultAsync(User admin, IReadOnlyList<HomeRowInstance> rows, CancellationToken ct);

    /// <summary>
    /// Finds a row in the user's effective layout.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="rowId">The row id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The row, or <c>null</c> if the user's effective layout has no such row.</returns>
    Task<EffectiveHomeRow?> FindRowAsync(User user, Guid rowId, CancellationToken ct);
}
