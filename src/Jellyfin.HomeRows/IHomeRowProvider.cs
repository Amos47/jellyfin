using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Schema;

namespace Jellyfin.HomeRows;

/// <summary>
/// A source of items for one kind of home row.
/// </summary>
public interface IHomeRowProvider
{
    /// <summary>
    /// Gets the stable provider id, see <see cref="HomeRowProviderIds"/>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the display name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the settings schema of this provider for the given context.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings schema.</returns>
    Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the items of a row.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="config">The validated row configuration.</param>
    /// <param name="paging">The paging parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row result.</returns>
    Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken);
}
