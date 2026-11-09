using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: stores the download requests of discovery films, and whether the discovery app has downloads enabled, in the
/// fork database.
/// </summary>
public interface IDiscoveryRequestStore
{
    /// <summary>
    /// Gets the request of a film.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request, or null if nobody requested the film.</returns>
    Task<DiscoveryRequest?> GetAsync(int tmdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the requests, most recently changed first.
    /// </summary>
    /// <param name="state">Only the requests in this state, or null for all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The requests.</returns>
    Task<IReadOnlyList<DiscoveryRequest>> GetAllAsync(string? state, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a user asked for a film: adds them as a requester, creates the request if there is none, and sets
    /// a failed or done request back to <see cref="DiscoveryRequest.StateRequested"/>. A request that is requested or
    /// downloading keeps its state.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request as it is now.</returns>
    Task<DiscoveryRequest> RequestAsync(int tmdbId, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the state of a request.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="state">The new state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request as it is now, or null if nobody requested the film.</returns>
    Task<DiscoveryRequest?> SetStateAsync(int tmdbId, string state, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the TMDB ids of the requests that aren't done.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TMDB ids.</returns>
    Task<IReadOnlySet<int>> GetActiveTmdbIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes a user from every request (the user was deleted). The requests stay.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets whether the discovery app has downloads enabled. Off until the app turns it on.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if downloads are enabled.</returns>
    Task<bool> GetDownloadsEnabledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets whether the discovery app has downloads enabled.
    /// </summary>
    /// <param name="enabled">Whether downloads are enabled.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetDownloadsEnabledAsync(bool enabled, CancellationToken cancellationToken);
}
