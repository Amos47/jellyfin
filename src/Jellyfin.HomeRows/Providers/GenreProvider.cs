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

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Movies or shows of one genre. The genre options come from the library.
/// </summary>
public sealed class GenreProvider : IHomeRowProvider
{
    private const string GenreKey = "genre";
    private const string MediaTypeKey = "mediaType";
    private const string SortKey = "sort";
    private const string LimitKey = "limit";
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const string DefaultCardStyle = "poster";

    private static readonly string[] _sorts = ["Random", "DateCreated", "PremiereDate", "SortName", "CommunityRating"];

    private readonly ILibraryManager _libraryManager;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="adapter">The item adapter.</param>
    public GenreProvider(ILibraryManager libraryManager, IHomeRowItemAdapter adapter)
    {
        _libraryManager = libraryManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.Genre;

    /// <inheritdoc />
    public string Name => "Genre";

    /// <inheritdoc />
    public string Description => "Movies or shows of one genre.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = GetGenres(context).Select(g => new HomeRowOption(g, g)).ToArray();
        return Task.FromResult(new HomeRowSchema(
        [
            new HomeRowSetting(GenreKey, HomeRowSettingType.Enum, "Genre") { Options = options },
            new HomeRowSetting(MediaTypeKey, HomeRowSettingType.Enum, "Type")
            {
                Default = JsonValue.Create("Movie"),
                Options = [new HomeRowOption("Movie", "Movies"), new HomeRowOption("Series", "Shows")]
            },
            new HomeRowSetting(SortKey, HomeRowSettingType.Enum, "Sort")
            {
                Default = JsonValue.Create("Random"),
                Options =
                [
                    new HomeRowOption("Random", "Random"),
                    new HomeRowOption("DateCreated", "Date added"),
                    new HomeRowOption("PremiereDate", "Release date"),
                    new HomeRowOption("SortName", "Name"),
                    new HomeRowOption("CommunityRating", "Community rating")
                ]
            },
            new HomeRowSetting(LimitKey, HomeRowSettingType.Integer, "Limit")
            {
                Default = JsonValue.Create(DefaultLimit),
                Minimum = 1,
                Maximum = MaxLimit
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
        var genre = GetString(config, GenreKey);

        // Names are compared case-insensitively, the stored value is passed to the query as-is.
        if (string.IsNullOrEmpty(genre)
            || !GetGenres(context).Contains(genre, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HomeRowResult([], null, presentation) { Title = genre, Error = "needs-configuration" });
        }

        var kind = GetString(config, MediaTypeKey) == "Series" ? BaseItemKind.Series : BaseItemKind.Movie;
        var sortName = GetString(config, SortKey);
        var sort = sortName is not null && _sorts.Contains(sortName) && Enum.TryParse<ItemSortBy>(sortName, out var parsed)
            ? parsed
            : ItemSortBy.Random;
        var limit = config[LimitKey] is JsonValue l && l.TryGetValue<int>(out var li)
            ? Math.Clamp(li, 1, MaxLimit)
            : DefaultLimit;

        var query = new InternalItemsQuery(context.User)
        {
            Recursive = true,
            IncludeItemTypes = [kind],
            Genres = [genre],
            OrderBy = [(sort, sort == ItemSortBy.SortName ? SortOrder.Ascending : SortOrder.Descending)],
            Limit = Math.Min(limit, paging.Limit),
            StartIndex = paging.StartIndex,
            DtoOptions = context.DtoOptions
        };

        var result = _libraryManager.GetItemsResult(query);
        var items = _adapter.ToDtos(result.Items, context);
        return Task.FromResult(new HomeRowResult(items, result.TotalRecordCount, presentation) { Title = genre });
    }

    private static string? GetString(JsonObject config, string key)
        => config[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private List<string> GetGenres(HomeRowContext context)
    {
        var query = new InternalItemsQuery(context.User)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true
        };
        return _libraryManager.GetGenres(query).Items
            .Select(t => t.Item.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
