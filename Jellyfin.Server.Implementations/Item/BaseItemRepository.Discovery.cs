#pragma warning disable RS0030 // Do not use banned APIs: Guid equality inside an EF expression is translated to SQL.

using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Fork: keeps the hidden films of the movie discovery feature out of every query unless the caller opts in.
/// </summary>
public sealed partial class BaseItemRepository
{
    private IDiscoveryFolderLocator? _discoveryFolderLocator;

    /// <summary>
    /// Leaves out everything in the discovery folder (TopParentId is the folder), the extras owned by such items and
    /// the Person items credited only on such items, unless <see cref="InternalItemsQuery.IncludeDiscovery"/> is set. A query for explicit
    /// <see cref="InternalItemsQuery.ItemIds"/> is not guarded: clients refetch items they already hold by id (the TV
    /// player, the trailer queue). Nor is a query for the extras of explicit <see cref="InternalItemsQuery.OwnerIds"/>:
    /// the caller already holds the owner, so a discovery film's detail page gets its local trailers.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="baseQuery">The query to filter.</param>
    /// <param name="filter">The query filter.</param>
    /// <returns>The filtered query.</returns>
    private IQueryable<BaseItemEntity> ApplyDiscoveryGuard(
        JellyfinDbContext context,
        IQueryable<BaseItemEntity> baseQuery,
        InternalItemsQuery filter)
    {
        if (filter.IncludeDiscovery || filter.ItemIds.Length > 0 || filter.OwnerIds.Length > 0)
        {
            return baseQuery;
        }

        var folderId = GetDiscoveryFolderId();
        if (folderId.IsEmpty())
        {
            return baseQuery;
        }

        baseQuery = baseQuery.Where(e => e.TopParentId != folderId
            && (e.OwnerId == null || !context.BaseItems.Any(o => o.Id == e.OwnerId && o.TopParentId == folderId)));

        // Person items carry no TopParentId: leave out the ones credited only on discovery items, by the same rule as
        // the people listing (PeopleRepository, ExcludeDiscoveryOnlyPeople). Uncredited people stay listed.
        var personType = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Person];
        var discoveryItemIds = context.BaseItems
            .Where(i => i.TopParentId == folderId
                || (i.OwnerId != null && context.BaseItems.Any(o => o.Id == i.OwnerId && o.TopParentId == folderId)))
            .Select(i => i.Id);
        return baseQuery.Where(e => e.Type != personType
            || !context.Peoples.Any(p => p.Name == e.Name && context.PeopleBaseItemMap.Any(m => m.PeopleId == p.Id))
            || context.Peoples.Any(p => p.Name == e.Name
                && context.PeopleBaseItemMap.Any(m => m.PeopleId == p.Id && !discoveryItemIds.Contains(m.ItemId))));
    }

    /// <inheritdoc />
    public IQueryable<BaseItemEntity> ApplyDiscoveryGuard(JellyfinDbContext context, IQueryable<BaseItemEntity> baseQuery)
        => ApplyDiscoveryGuard(context, baseQuery, new InternalItemsQuery());

    // Resolved on first use rather than injected so the upstream constructor stays as it is. Empty when the folder
    // is not registered yet (early boot) or there is no locator (unit tests with a mocked host).
    private Guid GetDiscoveryFolderId()
    {
        _discoveryFolderLocator ??= _appHost.Resolve<IDiscoveryFolderLocator>();
        return _discoveryFolderLocator?.FolderId ?? Guid.Empty;
    }
}
