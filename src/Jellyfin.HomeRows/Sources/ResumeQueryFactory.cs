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
    /// <param name="mediaType">The media type.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The limit.</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery Create(User user, ILibraryManager libraryManager, MediaType mediaType, int startIndex, int limit)
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
            AncestorIds = ancestorIds
        };
    }
}
