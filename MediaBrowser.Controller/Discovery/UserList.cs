using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a user's named list of films, such as their default list, Saved. Lists are per user and never shared.
/// </summary>
/// <param name="Id">The list id.</param>
/// <param name="UserId">The id of the user owning the list.</param>
/// <param name="Name">The list name.</param>
/// <param name="IsDefault">Whether this is the user's default list (Saved).</param>
/// <param name="Films">The films, newest first.</param>
public record UserList(Guid Id, Guid UserId, string Name, bool IsDefault, IReadOnlyList<UserListFilm> Films)
{
    /// <summary>
    /// The name of every user's default list.
    /// </summary>
    public const string DefaultName = "Saved";

    /// <summary>
    /// The source of the <see cref="DiscoveryList"/> a user list is shown as.
    /// </summary>
    public const string Source = "user";

    /// <summary>
    /// The prefix of a user list's page list id. Discovery list ids are <c>[a-z0-9-]</c>, so they never have it.
    /// </summary>
    public const string PageListIdPrefix = "user:";

    /// <summary>
    /// Gets the id of the list's page (<c>GET Discovery/Lists/{listId}/Page</c>): <c>user:</c> and the list id in "N"
    /// format.
    /// </summary>
    public string PageListId => GetPageListId(Id);

    /// <summary>
    /// Gets the page list id of a user list.
    /// </summary>
    /// <param name="listId">The user list id.</param>
    /// <returns>The page list id.</returns>
    public static string GetPageListId(Guid listId)
        => PageListIdPrefix + listId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets whether a page list id names a user list, and which one.
    /// </summary>
    /// <param name="pageListId">The page list id.</param>
    /// <param name="listId">The user list id, when it names one.</param>
    /// <returns>Whether the id has the user list prefix; a prefixed id that isn't a GUID names no list.</returns>
    public static bool IsPageListId(string pageListId, [NotNullWhen(true)] out Guid? listId)
    {
        ArgumentNullException.ThrowIfNull(pageListId);

        listId = null;
        if (!pageListId.StartsWith(PageListIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        listId = Guid.TryParse(pageListId.AsSpan(PageListIdPrefix.Length), out var id) ? id : Guid.Empty;
        return true;
    }

    /// <summary>
    /// Gets whether the list holds a film.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <returns>Whether the film is in the list.</returns>
    public bool Contains(int tmdbId) => Contains(tmdbId, DiscoveryMediaType.Movie);

    /// <summary>
    /// Gets whether the list holds a title of a type.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">The media type.</param>
    /// <returns>Whether the title is in the list.</returns>
    public bool Contains(int tmdbId, DiscoveryMediaType type) => Films.Any(f => f.TmdbId == tmdbId && f.Type == type);

    /// <summary>
    /// Gets the list as a <see cref="DiscoveryList"/>, so rows, pages and queues show it the way they show discovery
    /// lists: its page list id, its name, and its films newest first, each with its own type.
    /// </summary>
    /// <returns>The discovery list.</returns>
    public DiscoveryList ToDiscoveryList()
        => new(PageListId, Name, Source, Films.Select(f => new DiscoveryFilmRef(f.TmdbId, f.ImdbId, f.Type)).ToArray());
}
