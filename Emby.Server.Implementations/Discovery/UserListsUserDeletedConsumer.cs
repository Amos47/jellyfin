using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Events;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: removes a deleted user's lists, so the films only they saved stop being kept by the film sync.
/// </summary>
public sealed class UserListsUserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly IUserListStore _lists;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserListsUserDeletedConsumer"/> class.
    /// </summary>
    /// <param name="lists">The user list store.</param>
    public UserListsUserDeletedConsumer(IUserListStore lists)
    {
        _lists = lists;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);

        // Not cancellable: the user is already gone, so their lists must go too.
        return _lists.RemoveUserAsync(eventArgs.Argument.Id, CancellationToken.None);
    }
}
