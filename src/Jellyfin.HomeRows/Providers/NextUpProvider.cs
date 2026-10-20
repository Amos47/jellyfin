using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Schema;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Model.Querying;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// The Next Up row.
/// </summary>
public sealed class NextUpProvider : IHomeRowProvider
{
    private const string EnableRewatchingKey = "enableRewatching";
    private const string CutoffDaysKey = "nextUpCutoffDays";
    private const int DefaultCutoffDays = 365;
    private const string DefaultCardStyle = "thumb";

    private readonly NextUpSource _source;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextUpProvider"/> class.
    /// </summary>
    /// <param name="source">The next up source.</param>
    /// <param name="adapter">The item adapter.</param>
    public NextUpProvider(NextUpSource source, IHomeRowItemAdapter adapter)
    {
        _source = source;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.NextUp;

    /// <inheritdoc />
    public string Name => "Next Up";

    /// <inheritdoc />
    public string Description => "The next unwatched episode of the series you are watching.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HomeRowSchema(
        [
            new HomeRowSetting(EnableRewatchingKey, HomeRowSettingType.Boolean, "Rewatching")
            {
                Description = "Include the next episode of series you have already finished.",
                Default = JsonValue.Create(false)
            },
            new HomeRowSetting(CutoffDaysKey, HomeRowSettingType.Integer, "Cutoff in days")
            {
                Description = "Ignore series not watched within this many days. 0 disables the cutoff.",
                Default = JsonValue.Create(DefaultCutoffDays),
                Minimum = 0,
                Maximum = 3650
            },
            CardStyleSetting.Create(DefaultCardStyle)
        ]));
    }

    /// <inheritdoc />
    public Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paging);

        var rewatching = config[EnableRewatchingKey] is JsonValue r && r.TryGetValue<bool>(out var rb) && rb;
        var days = config[CutoffDaysKey] is JsonValue d && d.TryGetValue<int>(out var di) ? di : DefaultCutoffDays;

        var query = new NextUpQuery
        {
            User = context.User,
            StartIndex = paging.StartIndex,
            Limit = paging.Limit,
            EnableResumable = false,
            EnableRewatching = rewatching,
            NextUpDateCutoff = days > 0 ? context.UtcNow.AddDays(-days) : DateTime.MinValue,
            EnableTotalRecordCount = true
        };

        var result = _source.GetNextUp(query, context.DtoOptions);
        var items = _adapter.ToDtos(result.Items, context);

        return Task.FromResult(new HomeRowResult(items, result.TotalRecordCount, CardStyleSetting.ToPresentation(config, DefaultCardStyle)));
    }
}
