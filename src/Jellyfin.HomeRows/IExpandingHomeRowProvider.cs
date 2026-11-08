using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.HomeRows;

/// <summary>
/// A provider whose one layout row is shown as several rows. In the display view of a layout
/// (<c>GET HomeRows/Configuration?expand=true</c>) each enabled row of such a provider becomes one row per
/// <see cref="GetSubRowsAsync"/> sub-row, with an id derived from the parent id and the sub-row's key; the items of a
/// derived row are asked for with <see cref="HomeRowContext.SubRowKey"/> = that key.
/// </summary>
/// <remarks>
/// A derived id names its content, not its position: a client that keeps a row's title and refetches its items by id
/// later gets the same sub-row, even after the provider's sub-rows have changed, as long as the key is still in
/// <see cref="GetResolvableSubRowKeysAsync"/>. A derived id whose key is gone is asked for with
/// <see cref="HomeSubRow.UnknownKey"/>, or any other key the provider can't serve, and must get an empty result.
/// </remarks>
public interface IExpandingHomeRowProvider : IHomeRowProvider
{
    /// <summary>
    /// Gets the rows the row expands into for the context user now, in display order. None hides the row.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="config">The validated row configuration.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The sub-rows, capped by the caller at <see cref="Configuration.HomeRowConfigurationService.MaxExpandedRows"/>.</returns>
    Task<IReadOnlyList<HomeSubRow>> GetSubRowsAsync(HomeRowContext context, JsonObject config, CancellationToken ct);

    /// <summary>
    /// Gets every sub-row key whose items the provider can still serve, whether or not it is one of
    /// <see cref="GetSubRowsAsync"/> now. Used to map a derived row id back to its key.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="config">The validated row configuration.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The keys, in any order.</returns>
    Task<IReadOnlyList<string>> GetResolvableSubRowKeysAsync(HomeRowContext context, JsonObject config, CancellationToken ct);
}
