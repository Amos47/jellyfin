using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: stores the users' named lists of films in the fork database. Every user has a default list,
/// <see cref="UserList.DefaultName"/>, created the first time their lists are read. A user only ever reaches their
/// own lists: a list of another user reads as missing.
/// </summary>
public interface IUserListStore
{
    /// <summary>
    /// Gets a user's lists, the default one first, creating it if the user has none.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lists.</returns>
    Task<IReadOnlyList<UserList>> GetListsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a user's default list, creating it if the user has none.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The default list.</returns>
    Task<UserList> GetDefaultListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets one of a user's lists.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list, or null if the user has no list with that id.</returns>
    Task<UserList?> GetListAsync(Guid userId, Guid listId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a film (<see cref="DiscoveryMediaType.Movie"/>) to one of a user's lists. A film already in it keeps the
    /// date it was added.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="imdbId">The IMDb id, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>False if the user has no list with that id.</returns>
    Task<bool> AddFilmAsync(Guid userId, Guid listId, int tmdbId, string? imdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a title of a type to one of a user's lists. A title already in it keeps the date it was added.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">The media type.</param>
    /// <param name="imdbId">The IMDb id, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>False if the user has no list with that id.</returns>
    Task<bool> AddFilmAsync(Guid userId, Guid listId, int tmdbId, DiscoveryMediaType type, string? imdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a film (<see cref="DiscoveryMediaType.Movie"/>) from one of a user's lists.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>False if the user has no list with that id.</returns>
    Task<bool> RemoveFilmAsync(Guid userId, Guid listId, int tmdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a title of a type from one of a user's lists.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">The media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>False if the user has no list with that id.</returns>
    Task<bool> RemoveFilmAsync(Guid userId, Guid listId, int tmdbId, DiscoveryMediaType type, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a film (<see cref="DiscoveryMediaType.Movie"/>) to one of a user's lists if it isn't in it, else removes it, in one step: concurrent toggles each
    /// see the one before.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="imdbId">The IMDb id, if known, kept when the film is added.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the film is now in the list, false if it was removed, null if the user has no list with that id.</returns>
    Task<bool?> ToggleAsync(Guid userId, Guid listId, int tmdbId, string? imdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Toggles a title of a type in one of a user's lists, as
    /// <see cref="ToggleAsync(Guid, Guid, int, string?, CancellationToken)"/> does for a film.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">The media type.</param>
    /// <param name="imdbId">The IMDb id, if known, kept when the title is added.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the title is now in the list, false if it was removed, null if the user has no list with that id.</returns>
    Task<bool?> ToggleAsync(Guid userId, Guid listId, int tmdbId, DiscoveryMediaType type, string? imdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every list of a user, with its films (the user was deleted).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the TMDB ids of every film (<see cref="DiscoveryMediaType.Movie"/>) in any user's list.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TMDB ids.</returns>
    Task<IReadOnlySet<int>> GetListedTmdbIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the TMDB ids of every title of one type in any user's list.
    /// </summary>
    /// <param name="type">The media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TMDB ids.</returns>
    Task<IReadOnlySet<int>> GetListedTmdbIdsAsync(DiscoveryMediaType type, CancellationToken cancellationToken);
}
