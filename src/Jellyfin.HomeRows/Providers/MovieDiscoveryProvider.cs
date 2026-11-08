using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: Movie discovery. Expands into one row per discovery list picked for the user today, each titled with the
/// list's title. Which lists and films are shown is decided by <see cref="IDiscoveryRowPlanner"/>. A sub-row's key is
/// its list id, so a derived row id always serves the same list, even after the day's picks change.
/// </summary>
public sealed class MovieDiscoveryProvider : IExpandingHomeRowProvider
{
    private const string MaxRowsKey = "maxRows";
    private const string ExternalTrailersKey = "externalTrailers";
    private const int DefaultMaxRows = 3;
    private const int MaxMaxRows = 10;
    private const string DefaultCardStyle = "poster";

    private readonly IDiscoveryRowPlanner _planner;
    private readonly IDiscoveryStore _store;
    private readonly IHomeRowItemAdapter _adapter;
    private readonly ILogger<MovieDiscoveryProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieDiscoveryProvider"/> class.
    /// </summary>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="store">The discovery store, for the lists and the shown log.</param>
    /// <param name="adapter">The item adapter.</param>
    /// <param name="logger">The logger.</param>
    public MovieDiscoveryProvider(IDiscoveryRowPlanner planner, IDiscoveryStore store, IHomeRowItemAdapter adapter, ILogger<MovieDiscoveryProvider> logger)
    {
        _planner = planner;
        _store = store;
        _adapter = adapter;
        _logger = logger;
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
            new HomeRowSetting(ExternalTrailersKey, HomeRowSettingType.Boolean, "External trailers")
            {
                Description = "Show online trailers (such as YouTube) on films you don't own. Off shows only downloaded trailers.",
                Default = JsonValue.Create(false)
            },
            CardStyleSetting.Create(DefaultCardStyle)
        ]));

    /// <inheritdoc />
    public async Task<IReadOnlyList<HomeSubRow>> GetSubRowsAsync(HomeRowContext context, JsonObject config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        var lists = await _planner.GetPickedListsAsync(context.User, GetMaxRows(config), GetDay(context), ct).ConfigureAwait(false);
        return lists.Select(l => new HomeSubRow(l.Id)).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetResolvableSubRowKeysAsync(HomeRowContext context, JsonObject config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        // Every stored list, picked today or not: a client may still hold a row from an earlier pick.
        var lists = await _store.GetListsAsync(ct).ConfigureAwait(false);
        return lists.Select(l => l.Id).ToArray();
    }

    /// <inheritdoc />
    public async Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paging);

        var presentation = CardStyleSetting.ToPresentation(config, DefaultCardStyle);
        var day = GetDay(context);
        DiscoveryList? list;
        if (context.SubRowKey is null)
        {
            // The parent row's own id: today's first pick.
            var lists = await _planner.GetPickedListsAsync(context.User, GetMaxRows(config), day, cancellationToken).ConfigureAwait(false);
            list = lists.Count > 0 ? lists[0] : null;
        }
        else
        {
            // A derived row: the list it names, whether or not it is picked today.
            list = await _store.GetListAsync(context.SubRowKey, cancellationToken).ConfigureAwait(false);
        }

        if (list is null)
        {
            // Nothing picked today, or the list was deleted since the layout was fetched.
            return new HomeRowResult([], 0, presentation);
        }

        var items = _planner.GetRowItems(context.User, list);
        var page = items.Skip(paging.StartIndex).Take(paging.Limit).ToArray();
        if (page.Length > 0)
        {
            try
            {
                await _store.MarkShownAsync(page.Select(i => i.Film.TmdbId), day, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The shown log is best effort: failing to write it must not hide the row.
                _logger.LogWarning(ex, "Recording the shown discovery films of list {ListId} failed", list.Id);
            }
        }

        var dtos = _adapter.ToDtos(page.Select(i => i.Item).ToArray<BaseItem>(), context);
        return new HomeRowResult(dtos, items.Count, presentation) { Title = list.Title };
    }

    /// <summary>
    /// Gets the maximum rows of a Movie discovery row's config: its <c>maxRows</c> setting, else the default.
    /// </summary>
    /// <param name="config">The row config.</param>
    /// <returns>The maximum rows.</returns>
    internal static int GetMaxRows(JsonObject config)
        => config[MaxRowsKey] is JsonValue v && v.TryGetValue<int>(out var max)
            ? Math.Clamp(max, 1, MaxMaxRows)
            : DefaultMaxRows;

    /// <summary>
    /// Gets the <c>externalTrailers</c> setting of a Movie discovery row's config: false unless it is the boolean true.
    /// </summary>
    /// <param name="config">The row config.</param>
    /// <returns>Whether discovery films show their external trailers.</returns>
    internal static bool GetExternalTrailers(JsonObject config)
        => config[ExternalTrailersKey] is JsonValue v && v.TryGetValue<bool>(out var on) && on;

    // The pick changes at the server's local midnight.
    private static DateOnly GetDay(HomeRowContext context)
        => DateOnly.FromDateTime(context.UtcNow.ToLocalTime());
}
