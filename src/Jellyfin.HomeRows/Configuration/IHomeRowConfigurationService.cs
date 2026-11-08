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
    /// Deletes the admin default layout, so that users without a layout of their own fall back to their upstream
    /// home sections, else upstream's standard order. Does nothing if there is none.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the default is deleted.</returns>
    Task ResetAdminDefaultAsync(CancellationToken ct);

    /// <summary>
    /// Finds a row in the user's effective layout. The id may also be the derived id of a row an enabled
    /// <see cref="IExpandingHomeRowProvider"/> row expands into, for any key the provider can still serve
    /// (<see cref="IExpandingHomeRowProvider.GetResolvableSubRowKeysAsync"/>), not only today's; the parent row is then
    /// returned with <see cref="EffectiveHomeRow.SubRowKey"/> set.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="rowId">The row id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The row, or <c>null</c> if the user's effective layout has no such row.</returns>
    Task<EffectiveHomeRow?> FindRowAsync(User user, Guid rowId, CancellationToken ct);

    /// <summary>
    /// Turns a layout into its display view: each enabled row of an <see cref="IExpandingHomeRowProvider"/> becomes
    /// one row per sub-row the provider gives for the user (none removes it), with an id derived from the parent id and
    /// the sub-row's key, no title, and the parent's provider, config and enabled flag. Other rows are kept as they
    /// are. The result is for display only; saving it is rejected.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="layout">The effective layout.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The expanded layout.</returns>
    Task<EffectiveHomeRowLayout> ExpandAsync(User user, EffectiveHomeRowLayout layout, CancellationToken ct);
}
