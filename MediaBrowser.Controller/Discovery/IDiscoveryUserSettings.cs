using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a user's discovery settings, read from the Movie discovery rows (films) or the Show discovery rows (shows) of
/// their effective home layout.
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

    /// <summary>
    /// Gets the readiness bar the discovery lists are reported against: the largest <c>minReadyFilms</c> among the
    /// enabled Movie discovery rows of the stored layouts (the admin default and every user's own layout), or
    /// <see cref="DiscoveryPickRules.DefaultMinReadyFilms"/> when there is no such row.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The fewest playable films a list needs to be reported ready.</returns>
    Task<int> GetReadyThresholdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets whether discovery titles of a type show their external trailers to the user, as
    /// <see cref="GetExternalTrailersAsync(User, CancellationToken)"/> (the films') does, from the enabled discovery rows
    /// of that type: Movie discovery for films, Show discovery for shows.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="type">The media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the user sees external trailers on discovery titles of the type.</returns>
    Task<bool> GetExternalTrailersAsync(User user, DiscoveryMediaType type, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the readiness bar lists of a type are reported against, as <see cref="GetReadyThresholdAsync(CancellationToken)"/>
    /// (the Movie lists') does, from the discovery rows of that type.
    /// </summary>
    /// <param name="type">The media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The fewest playable titles a list of the type needs to be reported ready.</returns>
    Task<int> GetReadyThresholdAsync(DiscoveryMediaType type, CancellationToken cancellationToken);
}
