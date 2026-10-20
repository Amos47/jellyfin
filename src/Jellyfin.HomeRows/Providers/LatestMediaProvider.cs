using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// The "Recently Added" row of one library.
/// </summary>
public sealed class LatestMediaProvider : IHomeRowProvider
{
    private const string LibraryKey = "library";
    private const string DefaultCardStyle = "poster";

    private readonly IUserViewManager _userViewManager;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LatestMediaProvider"/> class.
    /// </summary>
    /// <param name="userViewManager">The user view manager.</param>
    /// <param name="adapter">The item adapter.</param>
    public LatestMediaProvider(IUserViewManager userViewManager, IHomeRowItemAdapter adapter)
    {
        _userViewManager = userViewManager;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.LatestMedia;

    /// <inheritdoc />
    public string Name => "Recently Added";

    /// <inheritdoc />
    public string Description => "The latest items added to a library.";

    /// <inheritdoc />
    public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = LatestViews.Get(context.User, _userViewManager)
            .Select(view => new HomeRowOption(view.Id.ToString("N"), view.Name))
            .ToArray();
        return Task.FromResult(new HomeRowSchema(
        [
            new HomeRowSetting(LibraryKey, HomeRowSettingType.Enum, "Library") { Options = options },
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
        var libraryId = config[LibraryKey] is JsonValue node && node.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : Guid.Empty;
        var library = libraryId.Equals(Guid.Empty)
            ? null
            : LatestViews.Get(context.User, _userViewManager).FirstOrDefault(v => v.Id.Equals(libraryId));
        if (library is null)
        {
            return Task.FromResult(new HomeRowResult([], null, presentation) { Error = "needs-configuration" });
        }

        // Fork copy of Jellyfin.Api/Controllers/UserLibraryController.cs at v12.2 (GetLatestMedia).
        var list = _userViewManager.GetLatestItems(
            new LatestItemsQuery
            {
                GroupItems = true,
                IsPlayed = context.User.HidePlayedInLatest ? false : null,
                Limit = paging.Limit,
                ParentId = library.Id,
                User = context.User
            },
            context.DtoOptions);

        var resolvedItems = new BaseItem[list.Count];
        var childCounts = new int[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var tuple = list[i];
            var item = tuple.Item2[0];
            var childCount = 0;

            if (tuple.Item1 is not null && (tuple.Item2.Count > 1 || tuple.Item1 is MusicAlbum))
            {
                item = tuple.Item1;
                childCount = tuple.Item2.Count;
            }

            resolvedItems[i] = item;
            childCounts[i] = childCount;
        }

        // The visibility check was already done in GetLatestItems. Restore the child counts afterwards.
        var dtos = _adapter.ToDtos(resolvedItems, context, skipVisibilityCheck: true);
        for (int i = 0; i < dtos.Count; i++)
        {
            if (childCounts[i] > 0)
            {
                dtos[i].ChildCount = childCounts[i];
            }
        }

        return Task.FromResult(new HomeRowResult(dtos, null, presentation) { Title = $"Recently Added in {library.Name}" });
    }
}
