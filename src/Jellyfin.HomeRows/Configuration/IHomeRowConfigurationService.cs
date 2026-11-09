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
    /// Gets the layout that applies to a user. Fork: a layout from upstream sections or upstream's standard order also
    /// gets the Saved row (<see cref="HomeRowProviderIds.UserList"/>, a fixed id) after its Movie discovery row or at the
    /// end, unless it is full; it is added on read, never stored.
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
    /// Gets every user's own stored layout. Users who inherit, and records that do not parse, are left out.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The layouts, each its rows in display order.</returns>
    Task<IReadOnlyList<IReadOnlyList<HomeRowInstance>>> GetUserLayoutsAsync(CancellationToken ct);

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
    /// returned with <see cref="EffectiveHomeRow.SubRowKey"/> set. An id with the parent's derived-row prefix but no
    /// such key (a deleted list) returns the parent with <see cref="HomeSubRow.UnknownKey"/>, so its items are empty.
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

    /// <summary>
    /// Fork: once per database, adds a <see cref="HomeRowProviderIds.UserList"/> row (Saved: default config, enabled,
    /// no title) to every stored layout, the users' and the admin default: right after its first Movie discovery row,
    /// or at the end when it has none. A layout that already has a user-list row, is full
    /// (<see cref="UpstreamHomeSectionMapper.MaxRows"/>) or doesn't parse is left as it is. A marker in the fork
    /// database records that the insert ran, so a row the user removes later never comes back. A full layout is
    /// logged. Layouts that aren't stored (the standard default and upstream sections) get the Saved row at read time
    /// instead (<see cref="GetEffectiveLayoutAsync"/>), never written.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True if the insert ran now, false if it had run before.</returns>
    Task<bool> InsertUserListRowsOnceAsync(CancellationToken ct);
}
