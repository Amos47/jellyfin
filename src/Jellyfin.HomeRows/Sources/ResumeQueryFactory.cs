// Fork copy of the resume query in Jellyfin.Api/Controllers/ItemsController.cs (GetResumeItems) at v12.2.
using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;

namespace Jellyfin.HomeRows.Sources;

/// <summary>
/// Builds the resume items query. Callers set <c>ExcludeItemTypes</c> and <c>DtoOptions</c> as needed.
/// </summary>
public static class ResumeQueryFactory
{
    /// <summary>
    /// Creates the resume query.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="sessionManager">The session manager. When set, the items the user is playing right now are excluded, as jellyfin-web asks for on the home screen (<c>excludeActiveSessions</c>); <c>null</c> excludes nothing.</param>
    /// <param name="mediaType">The media type.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The limit.</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery Create(User user, ILibraryManager libraryManager, ISessionManager? sessionManager, MediaType mediaType, int startIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var ancestorIds = Array.Empty<Guid>();
        var excludeFolderIds = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
        if (excludeFolderIds.Length > 0)
        {
            ancestorIds = libraryManager.GetUserRootFolder().GetChildren(user, true)
                .Where(i => i is Folder)
                .Where(i => !excludeFolderIds.Contains(i.Id))
                .Select(i => i.Id)
                .ToArray();
        }

        var excludeItemIds = Array.Empty<Guid>();
        if (sessionManager is not null)
        {
            // NowPlayingItem.Id is the displayed/primary id, but resume queries surface the actually-played
            // alternate version's own id. Expand each active session to every version id so an in-progress
            // alternate is excluded too, instead of leaking back into the resume list.
            excludeItemIds = sessionManager.Sessions
                .Where(s => s.UserId.Equals(user.Id) && s.NowPlayingItem is not null)
                .SelectMany(s => libraryManager.GetItemById(s.NowPlayingItem.Id) is Video video
                    ? video.GetAllVersions().Select(v => v.Id)
                    : [s.NowPlayingItem.Id])
                .Distinct()
                .ToArray();
        }

        return new InternalItemsQuery(user)
        {
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            IsResumable = true,
            StartIndex = startIndex,
            Limit = limit,
            ParentId = Guid.Empty,
            Recursive = true,
            MediaTypes = [mediaType],
            IsVirtualItem = false,
            CollapseBoxSetItems = false,
            IncludeOwnedItems = true,
            EnableTotalRecordCount = true,
            AncestorIds = ancestorIds,
            ExcludeItemIds = excludeItemIds
        };
    }
}
