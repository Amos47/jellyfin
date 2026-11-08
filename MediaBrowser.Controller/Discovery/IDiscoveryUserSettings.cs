using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a user's discovery settings, read from the Movie discovery rows of their effective home layout.
/// </summary>
public interface IDiscoveryUserSettings
{
    /// <summary>
    /// Gets whether discovery films show their external (remote, e.g. YouTube) trailers to the user: true when any
    /// enabled Movie discovery row of their effective layout has <c>externalTrailers</c> on. A user without such a
    /// row gets false. Owned films and local trailers are not affected.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the user sees external trailers on discovery films.</returns>
    Task<bool> GetExternalTrailersAsync(User user, CancellationToken cancellationToken);
}
