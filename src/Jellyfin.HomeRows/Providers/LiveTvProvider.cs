using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// The "On Now" Live TV row.
/// </summary>
public sealed class LiveTvProvider : IHomeRowProvider
{
    private readonly ILiveTvManager _liveTvManager;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvProvider"/> class.
    /// </summary>
    /// <param name="liveTvManager">The Live TV manager.</param>
    /// <param name="adapter">The item adapter.</param>
    public LiveTvProvider(ILiveTvManager liveTvManager, IHomeRowItemAdapter adapter)
    {
        _liveTvManager = liveTvManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.LiveTv;

    /// <inheritdoc />
    public string Name => "On Now";

    /// <inheritdoc />
    public string Description => "Live TV programs that are airing now.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new HomeRowSchema([]));

    /// <inheritdoc />
    public Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(paging);

        var presentation = new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary);
        if (!context.User.HasPermission(PermissionKind.EnableLiveTvAccess))
        {
            return Task.FromResult(new HomeRowResult([], 0, presentation));
        }

        // The Live TV manager has no start index, so fetch up to the end of the requested page and skip the
        // leading items. The total is the number of items the manager returned, which is all that paging can reach.
        var result = _liveTvManager.GetRecommendedProgramsInternal(
            new InternalItemsQuery(context.User) { IsAiring = true, Limit = paging.StartIndex + paging.Limit },
            context.DtoOptions,
            cancellationToken);
        var items = _adapter.ToDtos(result.Items.Skip(paging.StartIndex).ToList(), context);

        return Task.FromResult(new HomeRowResult(items, result.Items.Count, presentation));
    }
}
