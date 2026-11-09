using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: resolves the list id of a list page (<c>GET Discovery/Lists/{listId}/Page</c>, a row's
/// <c>PageListId</c>): a discovery list id, or <c>user:{guid}</c> for one of the user's own lists.
/// </summary>
public static class DiscoveryPageLists
{
    /// <summary>
    /// Gets the list a page list id names, as the user may see it.
    /// </summary>
    /// <param name="store">The discovery store.</param>
    /// <param name="userLists">The user list store.</param>
    /// <param name="user">The user.</param>
    /// <param name="listId">The page list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list, or null if there is none or it is another user's.</returns>
    public static async Task<DiscoveryList?> GetAsync(IDiscoveryStore store, IUserListStore userLists, User user, string listId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(userLists);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(listId);

        if (!UserList.IsPageListId(listId, out var userListId))
        {
            return await store.GetListAsync(listId, cancellationToken).ConfigureAwait(false);
        }

        if (userListId.Value.Equals(Guid.Empty))
        {
            return null;
        }

        var list = await userLists.GetListAsync(user.Id, userListId.Value, cancellationToken).ConfigureAwait(false);
        return list?.ToDiscoveryList();
    }

    /// <summary>
    /// Gets whether a page list id names a user list, whose page shows every film, playable or not.
    /// </summary>
    /// <param name="listId">The page list id.</param>
    /// <returns>Whether it names a user list.</returns>
    public static bool IsUserList(string listId) => UserList.IsPageListId(listId, out _);
}
