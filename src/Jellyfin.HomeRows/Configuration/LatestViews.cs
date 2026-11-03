using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// The user views that get a "latest media" row on the standard home screen.
/// </summary>
public static class LatestViews
{
    /// <summary>
    /// Gets the user's views, in view order, minus the user's <see cref="PreferenceKind.LatestItemExcludes"/> and
    /// minus the views jellyfin-web never shows latest items for: collection types playlists, boxsets and livetv,
    /// and channels (<see cref="Channel"/> folders, which have no collection type).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="userViewManager">The user view manager.</param>
    /// <returns>The views.</returns>
    public static IReadOnlyList<Folder> Get(User user, IUserViewManager userViewManager)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(userViewManager);

        var excludes = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
        return userViewManager.GetUserViews(new UserViewQuery { User = user })
            .Where(view => !excludes.Contains(view.Id) && !IsExcludedType(view))
            .ToArray();
    }

    private static bool IsExcludedType(Folder view)
        => view is Channel
            || (view as IHasCollectionType)?.CollectionType is CollectionType.playlists or CollectionType.boxsets or CollectionType.livetv;
}
