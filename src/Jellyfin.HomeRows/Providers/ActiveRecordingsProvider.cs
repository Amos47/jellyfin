using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// The row of recordings that are in progress.
/// </summary>
public sealed class ActiveRecordingsProvider : IHomeRowProvider
{
    private readonly ILiveTvManager _liveTvManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveRecordingsProvider"/> class.
    /// </summary>
    /// <param name="liveTvManager">The Live TV manager.</param>
    public ActiveRecordingsProvider(ILiveTvManager liveTvManager)
    {
        _liveTvManager = liveTvManager;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.ActiveRecordings;

    /// <inheritdoc />
    public string Name => "Recordings";

    /// <inheritdoc />
    public string Description => "Live TV recordings that are in progress.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new HomeRowSchema([]));

    /// <inheritdoc />
    public async Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(paging);

        var result = await _liveTvManager.GetRecordingsAsync(
            new RecordingQuery { UserId = context.User.Id, IsInProgress = true, StartIndex = paging.StartIndex, Limit = paging.Limit },
            context.DtoOptions).ConfigureAwait(false);

        return new HomeRowResult(result.Items, result.TotalRecordCount, new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary));
    }
}
