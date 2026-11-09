using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: tells when a download request is done. A request is done once the film is in a library
/// (<see cref="IDiscoveryFilmSync.FindOwned(int, string?)"/>); the server sets that itself, on each read of the
/// request (the film's actions, the requests list, the protected films), since it is the one that sees the film.
/// </summary>
public static class DiscoveryRequestDone
{
    /// <summary>
    /// Marks a request done if its film is now in a library.
    /// </summary>
    /// <param name="store">The request store.</param>
    /// <param name="filmSync">The film sync, to find the library film.</param>
    /// <param name="request">The request, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request as it is now, or null if there was none.</returns>
    public static async Task<DiscoveryRequest?> CheckAsync(
        IDiscoveryRequestStore store,
        IDiscoveryFilmSync filmSync,
        DiscoveryRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is not { IsActive: true } || !IsOwned(filmSync, request.TmdbId))
        {
            return request;
        }

        return await store.SetStateAsync(request.TmdbId, DiscoveryRequest.StateDone, cancellationToken).ConfigureAwait(false) ?? request;
    }

    /// <summary>
    /// Marks done each request whose film is now in a library.
    /// </summary>
    /// <param name="store">The request store.</param>
    /// <param name="filmSync">The film sync, to find the library films.</param>
    /// <param name="requests">The requests.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The requests as they are now, in the same order.</returns>
    public static async Task<IReadOnlyList<DiscoveryRequest>> CheckAllAsync(
        IDiscoveryRequestStore store,
        IDiscoveryFilmSync filmSync,
        IEnumerable<DiscoveryRequest> requests,
        CancellationToken cancellationToken)
    {
        var checkedRequests = new List<DiscoveryRequest>();
        foreach (var request in requests)
        {
            checkedRequests.Add((await CheckAsync(store, filmSync, request, cancellationToken).ConfigureAwait(false))!);
        }

        return checkedRequests;
    }

    /// <summary>
    /// Gets whether a film is in a library, matched by TMDB id or by the IMDb id its discovery film has.
    /// </summary>
    /// <param name="filmSync">The film sync.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <returns>True if a library has the film.</returns>
    public static bool IsOwned(IDiscoveryFilmSync filmSync, int tmdbId)
    {
        System.ArgumentNullException.ThrowIfNull(filmSync);

        var imdbId = filmSync.FindFilm(tmdbId)?.GetProviderId(MetadataProvider.Imdb);
        return filmSync.FindOwned(tmdbId, string.IsNullOrEmpty(imdbId) ? null : imdbId) is not null;
    }
}
