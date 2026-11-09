using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Events;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: removes a deleted user's lists, so the films only they saved stop being kept by the film sync, and takes them
/// off the download requests (the requests themselves stay: the app may already be downloading them).
/// </summary>
public sealed class UserListsUserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly IUserListStore _lists;
    private readonly IDiscoveryRequestStore _requests;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserListsUserDeletedConsumer"/> class.
    /// </summary>
    /// <param name="lists">The user list store.</param>
    /// <param name="requests">The download request store.</param>
    public UserListsUserDeletedConsumer(IUserListStore lists, IDiscoveryRequestStore requests)
    {
        _lists = lists;
        _requests = requests;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);

        // Not cancellable: the user is already gone, so their lists must go too.
        await _lists.RemoveUserAsync(eventArgs.Argument.Id, CancellationToken.None).ConfigureAwait(false);
        await _requests.RemoveUserAsync(eventArgs.Argument.Id, CancellationToken.None).ConfigureAwait(false);
    }
}
