using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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

        var result = _liveTvManager.GetRecommendedProgramsInternal(
            new InternalItemsQuery(context.User) { IsAiring = true, Limit = paging.Limit },
            context.DtoOptions,
            cancellationToken);
        var items = _adapter.ToDtos(result.Items, context);

        return Task.FromResult(new HomeRowResult(items, result.TotalRecordCount, new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary)));
    }
}
