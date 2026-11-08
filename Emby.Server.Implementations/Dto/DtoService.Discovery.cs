using System;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Dto;

/// <summary>
/// Fork: hides the external (remote) trailers of discovery films from users whose Movie discovery row has
/// <c>externalTrailers</c> off, the default. Owned films and local trailers are unchanged.
/// </summary>
public partial class DtoService
{
    private IDiscoveryFolderLocator? _discoveryFolderLocator;
    private IDiscoveryUserSettings? _discoveryUserSettings;

    /// <summary>
    /// Empties the remote trailers of a discovery film's DTO unless the user's setting allows them. Cheap for every
    /// other item: the user's layout is only read for a discovery film that has remote trailers, once per
    /// <paramref name="externalTrailers"/> cache (one DTO call).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="dto">Its DTO.</param>
    /// <param name="user">The user, or null.</param>
    /// <param name="externalTrailers">The user's setting, once looked up; null before.</param>
    private void HideDiscoveryRemoteTrailers(BaseItem item, BaseItemDto dto, User? user, ref bool? externalTrailers)
    {
        if (dto.RemoteTrailers is not { Count: > 0 } || !IsDiscoveryFilm(item))
        {
            return;
        }

        externalTrailers ??= GetExternalTrailers(user);
        if (!externalTrailers.Value)
        {
            dto.RemoteTrailers = [];
        }
    }

    // A virtual film directly in the discovery folder, by the rule the query guard and the trailer code use. Resolved
    // on first use rather than injected so the upstream constructor stays as it is.
    private bool IsDiscoveryFilm(BaseItem item)
    {
        if (item is not Movie { IsVirtualItem: true })
        {
            return false;
        }

        _discoveryFolderLocator ??= _appHost.Resolve<IDiscoveryFolderLocator>();
        var folderId = _discoveryFolderLocator?.FolderId ?? Guid.Empty;
        return !folderId.Equals(Guid.Empty) && item.ParentId.Equals(folderId);
    }

    // No user, no settings service or a failed lookup all mean the default: hidden.
    private bool GetExternalTrailers(User? user)
    {
        if (user is null)
        {
            return false;
        }

        _discoveryUserSettings ??= _appHost.Resolve<IDiscoveryUserSettings>();
        if (_discoveryUserSettings is null)
        {
            return false;
        }

        try
        {
            // DtoService is synchronous; the layout read is a small fork-database query.
            return _discoveryUserSettings.GetExternalTrailersAsync(user, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the discovery settings of user {UserId} failed; hiding external trailers", user.Id);
            return false;
        }
    }
}
