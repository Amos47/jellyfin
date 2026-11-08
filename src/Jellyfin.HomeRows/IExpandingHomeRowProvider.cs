using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.HomeRows;

/// <summary>
/// A provider whose one layout row is shown as several rows. In the display view of a layout
/// (<c>GET HomeRows/Configuration?expand=true</c>) each enabled row of such a provider becomes
/// <see cref="GetRowCountAsync"/> rows with derived ids; the items of derived row <c>i</c> are asked for with
/// <see cref="HomeRowContext.SubRow"/> = <c>i</c>.
/// </summary>
public interface IExpandingHomeRowProvider : IHomeRowProvider
{
    /// <summary>
    /// Gets how many rows the row expands into for the context user. Zero hides the row.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="config">The validated row configuration.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of rows, capped by the caller at <see cref="Configuration.HomeRowConfigurationService.MaxExpandedRows"/>.</returns>
    Task<int> GetRowCountAsync(HomeRowContext context, JsonObject config, CancellationToken ct);
}
