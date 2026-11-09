using System;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Dto;

/// <summary>
/// Fork: hides the external (remote) trailers of discovery films from users whose Movie discovery row has
/// <c>externalTrailers</c> off, the default, and those of discovery shows by the Show discovery row's setting. Owned
/// titles and local trailers are unchanged.
/// </summary>
public partial class DtoService
{
    private IDiscoveryFolderLocator? _discoveryFolderLocator;
    private IDiscoveryUserSettings? _discoveryUserSettings;

    /// <summary>
    /// Empties the remote trailers of a discovery title's DTO unless the user's setting for its type allows them. Cheap
    /// for every other item: the user's layout is only read for a discovery title that has remote trailers, once per
    /// type per <paramref name="externalTrailers"/> cache (one DTO call).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="dto">Its DTO.</param>
    /// <param name="user">The user, or null.</param>
    /// <param name="externalTrailers">The user's settings, once looked up.</param>
    private void HideDiscoveryRemoteTrailers(BaseItem item, BaseItemDto dto, User? user, DiscoveryExternalTrailers externalTrailers)
    {
        if (dto.RemoteTrailers is not { Count: > 0 } || !IsDiscoveryFilm(item))
        {
            return;
        }

        bool allowed;
        if (item is Series)
        {
            externalTrailers.Series ??= GetExternalTrailers(user, DiscoveryMediaType.Series);
            allowed = externalTrailers.Series.Value;
        }
        else
        {
            externalTrailers.Movie ??= GetExternalTrailers(user, DiscoveryMediaType.Movie);
            allowed = externalTrailers.Movie.Value;
        }

        if (!allowed)
        {
            dto.RemoteTrailers = [];
        }
    }

    // A virtual film or show directly in the discovery folder, by the rule the query guard and the trailer code use.
    // Resolved on first use rather than injected so the upstream constructor stays as it is.
    private bool IsDiscoveryFilm(BaseItem item)
    {
        if (item is not (Movie or Series) || !item.IsVirtualItem)
        {
            return false;
        }

        _discoveryFolderLocator ??= _appHost.Resolve<IDiscoveryFolderLocator>();
        var folderId = _discoveryFolderLocator?.FolderId ?? Guid.Empty;
        return !folderId.Equals(Guid.Empty) && item.ParentId.Equals(folderId);
    }

    // No user, no settings service or a failed lookup all mean the default: hidden.
    private bool GetExternalTrailers(User? user, DiscoveryMediaType type)
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
            return type == DiscoveryMediaType.Movie
                ? _discoveryUserSettings.GetExternalTrailersAsync(user, CancellationToken.None).GetAwaiter().GetResult()
                : _discoveryUserSettings.GetExternalTrailersAsync(user, type, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the discovery settings of user {UserId} failed; hiding external trailers", user.Id);
            return false;
        }
    }

    // The user's external-trailer settings of one DTO call, per type; null until looked up.
    private sealed class DiscoveryExternalTrailers
    {
        public bool? Movie { get; set; }

        public bool? Series { get; set; }
    }
}
