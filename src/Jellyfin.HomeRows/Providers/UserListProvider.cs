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
/// Fork: one of the user's named lists, such as Saved. Its <c>list</c> setting names the list: <c>default</c> (the
/// default) for the user's Saved, else the id of one of the user's lists. It shows every film of the list the user may
/// see, newest first, even films without a trailer, titled with the list's name, and names the list's page in
/// <see cref="HomeRowPresentation.PageListId"/> (<c>user:{guid}</c>). A list that isn't the user's shows nothing.
/// </summary>
public sealed class UserListProvider : IHomeRowProvider
{
    /// <summary>
    /// The <c>list</c> value naming the user's default list.
    /// </summary>
    public const string DefaultList = "default";

    private const string ListKey = "list";
    private const string DefaultCardStyle = "poster";

    private readonly IUserListStore _lists;
    private readonly IDiscoveryRowPlanner _planner;
    private readonly IHomeRowItemAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserListProvider"/> class.
    /// </summary>
    /// <param name="lists">The user list store.</param>
    /// <param name="planner">The discovery row planner, which resolves films as the user sees them.</param>
    /// <param name="adapter">The item adapter.</param>
    public UserListProvider(IUserListStore lists, IDiscoveryRowPlanner planner, IHomeRowItemAdapter adapter)
    {
        _lists = lists;
        _planner = planner;
        _adapter = adapter;
    }

    /// <inheritdoc />
    public string Id => HomeRowProviderIds.UserList;

    /// <inheritdoc />
    public string Name => "My list";

    /// <inheritdoc />
    public string Description => "Films you saved to one of your lists, newest first.";

    /// <inheritdoc />
    public async Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The user's own lists: the default one by its fixed value (so a layout shared as the admin default names each
        // user's own Saved), the others by id.
        var lists = await _lists.GetListsAsync(context.User.Id, cancellationToken).ConfigureAwait(false);
        var options = lists
            .Select(l => l.IsDefault ? new HomeRowOption(DefaultList, l.Name) : new HomeRowOption(l.Id.ToString("N"), l.Name))
            .ToArray();
        return new HomeRowSchema(
        [
            new HomeRowSetting(ListKey, HomeRowSettingType.Enum, "List")
            {
                Default = JsonValue.Create(DefaultList),
                Options = options
            },
            CardStyleSetting.Create(DefaultCardStyle)
        ]);
    }

    /// <inheritdoc />
    public async Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paging);

        var presentation = CardStyleSetting.ToPresentation(config, DefaultCardStyle);
        var value = config[ListKey] is JsonValue v && v.TryGetValue<string>(out var text) ? text : DefaultList;
        var list = string.Equals(value, DefaultList, StringComparison.Ordinal)
            ? await _lists.GetDefaultListAsync(context.User.Id, cancellationToken).ConfigureAwait(false)
            : Guid.TryParse(value, out var listId)
                ? await _lists.GetListAsync(context.User.Id, listId, cancellationToken).ConfigureAwait(false)
                : null;
        if (list is null)
        {
            return new HomeRowResult([], 0, presentation);
        }

        // Every film the user may see, playable or not: a saved film stays in view until it is removed.
        var items = _planner.GetRowItems(context.User, list.ToDiscoveryList(), false);
        var page = items.Skip(paging.StartIndex).Take(paging.Limit).Select(i => i.Item).ToArray<BaseItem>();
        var dtos = _adapter.ToDtos(page, context);
        return new HomeRowResult(dtos, items.Count, presentation with { PageListId = list.PageListId }) { Title = list.Name };
    }
}
