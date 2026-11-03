using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.HomeRows.Providers.NewReleases;

/// <summary>
/// Recently released, unwatched movies and episodes, one card per series.
/// </summary>
public sealed class NewReleasesProvider : IHomeRowProvider
{
    private const string DaysKey = "days";
    private const string MediaTypesKey = "mediaTypes";
    private const int DefaultDays = 30;
    private const int QueryLimit = 200;
    private const string DefaultCardStyle = "thumb";
    private const string Title = "New Releases";

    private readonly ILibraryManager _libraryManager;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewReleasesProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="adapter">The item adapter.</param>
    public NewReleasesProvider(ILibraryManager libraryManager, IHomeRowItemAdapter adapter)
    {
        _libraryManager = libraryManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.NewReleases;

    /// <inheritdoc />
    public string Name => Title;

    /// <inheritdoc />
    public string Description => "Recently released movies and episodes you have not watched, one card per series.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HomeRowSchema(
        [
            new HomeRowSetting(DaysKey, HomeRowSettingType.Integer, "Days")
            {
                Description = "Show items released within this many days.",
                Default = JsonValue.Create(DefaultDays),
                Minimum = 1,
                Maximum = 365
            },
            new HomeRowSetting(MediaTypesKey, HomeRowSettingType.MultiEnum, "Types")
            {
                Description = "Pick at least one type.",
                Default = new JsonArray("Movie", "Episode"),
                MinItems = 1,
                Options = [new HomeRowOption("Movie", "Movies"), new HomeRowOption("Episode", "Episodes")]
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

        var presentation = CardStyleSetting.ToPresentation(config, DefaultCardStyle);
        var kinds = GetKinds(config);
        if (kinds.Count == 0)
        {
            return Task.FromResult(new HomeRowResult([], null, presentation) { Title = Title, Error = "needs-configuration" });
        }

        var days = config[DaysKey] is JsonValue d && d.TryGetValue<int>(out var di) ? di : DefaultDays;
        var query = new InternalItemsQuery(context.User)
        {
            Recursive = true,
            IncludeItemTypes = kinds.ToArray(),
            MinPremiereDate = context.UtcNow.AddDays(-days),
            MaxPremiereDate = context.UtcNow,
            IsPlayed = false,
            IsMissing = false,
            OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Descending)],
            Limit = QueryLimit,
            DtoOptions = context.DtoOptions
        };

        var collapsed = NewReleasesCollapser.Collapse(_libraryManager.GetItemsResult(query).Items);
        var page = collapsed.Skip(paging.StartIndex).Take(paging.Limit).ToList();
        var items = _adapter.ToDtos(page, context);

        return Task.FromResult(new HomeRowResult(items, collapsed.Count, presentation) { Title = Title });
    }

    private static List<BaseItemKind> GetKinds(JsonObject config)
    {
        if (config[MediaTypesKey] is not JsonArray array)
        {
            return [BaseItemKind.Movie, BaseItemKind.Episode];
        }

        var kinds = new List<BaseItemKind>();
        foreach (var node in array)
        {
            var kind = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (kind == "Movie")
            {
                kinds.Add(BaseItemKind.Movie);
            }
            else if (kind == "Episode")
            {
                kinds.Add(BaseItemKind.Episode);
            }
        }

        return kinds;
    }
}
