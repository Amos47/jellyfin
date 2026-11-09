using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: keeps the virtual discovery films in step with the discovery lists.
/// </summary>
public interface IDiscoveryFilmSync
{
    /// <summary>
    /// Makes sure a virtual film exists for every TMDB id in any list, and deletes the virtual films (and their
    /// extras) that are in no list. Films in any user's list (<see cref="IUserListStore"/>) are kept, not created.
    /// Library films are never touched.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SyncAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the virtual discovery film of a TMDB id.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <returns>The film, or null if there is none.</returns>
    Movie? FindFilm(int tmdbId);

    /// <summary>
    /// Gets the virtual discovery film with a matching IMDb id.
    /// </summary>
    /// <param name="imdbId">The IMDb id.</param>
    /// <returns>The film, or null if there is none.</returns>
    Movie? FindFilmByImdb(string imdbId);

    /// <summary>
    /// Gets the film in the user's library with a matching TMDB or IMDb id.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="imdbId">The IMDb id, if known.</param>
    /// <returns>The library film, or null if the user has none.</returns>
    BaseItem? FindOwned(User user, int tmdbId, string? imdbId);
}
