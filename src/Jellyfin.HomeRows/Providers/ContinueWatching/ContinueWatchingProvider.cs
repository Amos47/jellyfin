using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Schema;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;

namespace Jellyfin.HomeRows.Providers.ContinueWatching;

/// <summary>
/// The combined Continue Watching row: in-progress items and next up episodes, one card per series.
/// </summary>
public sealed class ContinueWatchingProvider : IHomeRowProvider
{
    private const string IncludeMoviesKey = "includeMovies";
    private const string EnableRewatchingKey = "enableRewatching";
    private const string CutoffDaysKey = "nextUpCutoffDays";
    private const int DefaultCutoffDays = 365;
    private const int QueryLimit = 50;
    private const int RecentlyPlayedLimit = 100;
    private const string DefaultCardStyle = "thumb";

    private readonly NextUpSource _nextUpSource;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinueWatchingProvider"/> class.
    /// </summary>
    /// <param name="nextUpSource">The next up source.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="adapter">The item adapter.</param>
    public ContinueWatchingProvider(
        NextUpSource nextUpSource,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        IHomeRowItemAdapter adapter)
    {
        _nextUpSource = nextUpSource;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.ContinueWatching;

    /// <inheritdoc />
    public string Name => "Continue Watching";

    /// <inheritdoc />
    public string Description => "Partly watched videos and the next episode of series you are watching, one card per series.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HomeRowSchema(
        [
            new HomeRowSetting(IncludeMoviesKey, HomeRowSettingType.Boolean, "Movies")
            {
                Description = "Include partly watched movies.",
                Default = JsonValue.Create(true)
            },
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

        var includeMovies = !(config[IncludeMoviesKey] is JsonValue m && m.TryGetValue<bool>(out var mb) && !mb);
        var rewatching = config[EnableRewatchingKey] is JsonValue r && r.TryGetValue<bool>(out var rb) && rb;
        var days = config[CutoffDaysKey] is JsonValue d && d.TryGetValue<int>(out var di) ? di : DefaultCutoffDays;
        var user = context.User;
        var queryOptions = WithUserData(context.DtoOptions);

        var resumeQuery = ResumeQueryFactory.Create(user, _libraryManager, _sessionManager, MediaType.Video, 0, QueryLimit);
        resumeQuery.ExcludeItemTypes = includeMovies ? [BaseItemKind.AudioBook] : [BaseItemKind.AudioBook, BaseItemKind.Movie];
        resumeQuery.DtoOptions = queryOptions;
        var resume = _libraryManager.GetItemsResult(resumeQuery).Items;

        var nextUp = _nextUpSource.GetNextUp(
            new NextUpQuery
            {
                User = user,
                Limit = QueryLimit,
                EnableResumable = false,
                EnableRewatching = rewatching,
                NextUpDateCutoff = NextUpProvider.ResolveCutoff(context, days),
                EnableTotalRecordCount = false
            },
            queryOptions).Items;

        var recentQuery = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Episode],
            IsPlayed = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = RecentlyPlayedLimit,
            DtoOptions = queryOptions
        };
        var recent = _libraryManager.GetItemsResult(recentQuery).Items;

        var combined = ContinueWatchingCombiner.Combine(
            resume.Select(i => ToCandidate(user, i)).ToList(),
            nextUp.Select(i => ToCandidate(user, i)).ToList(),
            recent.Select(i => ToCandidate(user, i)).ToList());

        var page = combined.Skip(paging.StartIndex).Take(paging.Limit).ToList();
        var items = _adapter.ToDtos(page, context);

        return Task.FromResult(new HomeRowResult(items, combined.Count, CardStyleSetting.ToPresentation(config, DefaultCardStyle))
        {
            Title = "Continue Watching"
        });
    }

    /// <summary>
    /// Combining needs each item's last played date, which the item query only loads when user data is enabled.
    /// A client can turn that off for its response, so the internal queries get their own options. They keep the
    /// client's other settings because the same item instances are turned into DTOs afterwards.
    /// </summary>
    private static DtoOptions WithUserData(DtoOptions clientOptions)
    {
        if (clientOptions.EnableUserData)
        {
            return clientOptions;
        }

        return new DtoOptions(false)
        {
            Fields = clientOptions.Fields,
            ImageTypes = clientOptions.ImageTypes,
            ImageTypeLimit = clientOptions.ImageTypeLimit,
            EnableImages = clientOptions.EnableImages,
            AddProgramRecordingInfo = clientOptions.AddProgramRecordingInfo,
            AddCurrentProgram = clientOptions.AddCurrentProgram,
            EnableUserData = true
        };
    }

    private ContinueWatchingCandidate ToCandidate(User user, BaseItem item)
    {
        Guid? seriesId = item is Episode { SeriesId: var id } && !id.Equals(Guid.Empty) ? id : null;
        return new ContinueWatchingCandidate(item, seriesId, _userDataManager.GetUserData(user, item)?.LastPlayedDate);
    }
}
