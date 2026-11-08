using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: Movie discovery. Expands into one row per discovery list picked for the user today, each titled with the
/// list's title. Which lists and films are shown is decided by <see cref="IDiscoveryRowPlanner"/>.
/// </summary>
public sealed class MovieDiscoveryProvider : IExpandingHomeRowProvider
{
    private const string MaxRowsKey = "maxRows";
    private const int DefaultMaxRows = 3;
    private const int MaxMaxRows = 10;
    private const string DefaultCardStyle = "poster";

    private readonly IDiscoveryRowPlanner _planner;
    private readonly IDiscoveryStore _store;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieDiscoveryProvider"/> class.
    /// </summary>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="store">The discovery store, for the shown log.</param>
    /// <param name="adapter">The item adapter.</param>
    public MovieDiscoveryProvider(IDiscoveryRowPlanner planner, IDiscoveryStore store, IHomeRowItemAdapter adapter)
    {
        _planner = planner;
        _store = store;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.MovieDiscovery;

    /// <inheritdoc />
    public string Name => "Movie discovery";

    /// <inheritdoc />
    public string Description => "Films you don't own, from lists picked for you each day.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new HomeRowSchema(
        [
            new HomeRowSetting(MaxRowsKey, HomeRowSettingType.Integer, "Maximum rows")
            {
                Default = JsonValue.Create(DefaultMaxRows),
                Minimum = 1,
                Maximum = MaxMaxRows
            },
            CardStyleSetting.Create(DefaultCardStyle)
        ]));

    /// <inheritdoc />
    public async Task<int> GetRowCountAsync(HomeRowContext context, JsonObject config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        var lists = await _planner.GetPickedListsAsync(context.User, GetMaxRows(config), GetDay(context), ct).ConfigureAwait(false);
        return lists.Count;
    }

    /// <inheritdoc />
    public async Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paging);

        var presentation = CardStyleSetting.ToPresentation(config, DefaultCardStyle);
        var day = GetDay(context);
        var lists = await _planner.GetPickedListsAsync(context.User, GetMaxRows(config), day, cancellationToken).ConfigureAwait(false);
        if (context.SubRow < 0 || context.SubRow >= lists.Count)
        {
            // Today's pick has fewer lists than this row's index (the lists changed since the layout was fetched).
            return new HomeRowResult([], 0, presentation);
        }

        var list = lists[context.SubRow];
        var items = _planner.GetRowItems(context.User, list);
        var page = items.Skip(paging.StartIndex).Take(paging.Limit).ToArray();
        if (page.Length > 0)
        {
            await _store.MarkShownAsync(page.Select(i => i.Film.TmdbId), day, cancellationToken).ConfigureAwait(false);
        }

        var dtos = _adapter.ToDtos(page.Select(i => i.Item).ToArray<BaseItem>(), context);
        return new HomeRowResult(dtos, items.Count, presentation) { Title = list.Title };
    }

    private static int GetMaxRows(JsonObject config)
        => config[MaxRowsKey] is JsonValue v && v.TryGetValue<int>(out var max)
            ? Math.Clamp(max, 1, MaxMaxRows)
            : DefaultMaxRows;

    // The pick changes at the server's local midnight.
    private static DateOnly GetDay(HomeRowContext context)
        => DateOnly.FromDateTime(context.UtcNow.ToLocalTime());
}
