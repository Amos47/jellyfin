using System;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: discovery titles by media type. A <see cref="Movie"/> item is a <see cref="DiscoveryMediaType.Movie"/> title
/// and a <see cref="Series"/> item a <see cref="DiscoveryMediaType.Series"/> one; nothing else is a discovery title.
/// The typed lookups route a Movie to the film members of <see cref="IDiscoveryFilmSync"/> (unchanged from before
/// shows) and a Series to its series members, so a TMDB id is only ever matched within its type.
/// </summary>
public static class DiscoveryTitles
{
    /// <summary>
    /// Gets the media type of an item: Movie for a film, Series for a show, else null.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The type, or null if the item is no discovery title type.</returns>
    public static DiscoveryMediaType? GetMediaType(BaseItem item)
        => item switch
        {
            Movie => DiscoveryMediaType.Movie,
            Series => DiscoveryMediaType.Series,
            _ => null,
        };

    /// <summary>
    /// Gets the item kind of a media type.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The item kind.</returns>
    public static BaseItemKind ToItemKind(DiscoveryMediaType type)
        => type == DiscoveryMediaType.Series ? BaseItemKind.Series : BaseItemKind.Movie;

    /// <summary>
    /// Gets the TMDB id and media type of a film or a show, from its TMDB provider id.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="tmdbId">The positive TMDB id.</param>
    /// <param name="type">The media type.</param>
    /// <returns>Whether the item is a film or a show with a positive TMDB id.</returns>
    public static bool TryGetTmdbId(BaseItem item, out int tmdbId, out DiscoveryMediaType type)
    {
        ArgumentNullException.ThrowIfNull(item);

        tmdbId = 0;
        type = DiscoveryMediaType.Movie;
        if (GetMediaType(item) is not { } itemType
            || !int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            return false;
        }

        tmdbId = id;
        type = itemType;
        return true;
    }

    /// <summary>
    /// Gets the virtual discovery title of a TMDB id and type.
    /// </summary>
    /// <param name="sync">The film sync.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="type">The media type.</param>
    /// <returns>The virtual film or show, or null if there is none.</returns>
    public static BaseItem? FindTitle(this IDiscoveryFilmSync sync, int tmdbId, DiscoveryMediaType type)
    {
        ArgumentNullException.ThrowIfNull(sync);
        return type == DiscoveryMediaType.Series ? sync.FindSeries(tmdbId) : sync.FindFilm(tmdbId);
    }

    /// <summary>
    /// Gets the virtual discovery title of a type with a matching IMDb id.
    /// </summary>
    /// <param name="sync">The film sync.</param>
    /// <param name="imdbId">The IMDb id.</param>
    /// <param name="type">The media type.</param>
    /// <returns>The virtual film or show, or null if there is none.</returns>
    public static BaseItem? FindTitleByImdb(this IDiscoveryFilmSync sync, string imdbId, DiscoveryMediaType type)
    {
        ArgumentNullException.ThrowIfNull(sync);
        return type == DiscoveryMediaType.Series ? sync.FindSeriesByImdb(imdbId) : sync.FindFilmByImdb(imdbId);
    }

    /// <summary>
    /// Gets the title of a type in the user's library with a matching TMDB or IMDb id.
    /// </summary>
    /// <param name="sync">The film sync.</param>
    /// <param name="user">The user.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="imdbId">The IMDb id, if known.</param>
    /// <param name="type">The media type.</param>
    /// <returns>The library film or show, or null if the user has none.</returns>
    public static BaseItem? FindOwnedTitle(this IDiscoveryFilmSync sync, User user, int tmdbId, string? imdbId, DiscoveryMediaType type)
    {
        ArgumentNullException.ThrowIfNull(sync);
        return type == DiscoveryMediaType.Series ? sync.FindOwnedSeries(user, tmdbId, imdbId) : sync.FindOwned(user, tmdbId, imdbId);
    }

    /// <summary>
    /// Gets a title of a type in any library with a matching TMDB or IMDb id, whoever may see it.
    /// </summary>
    /// <param name="sync">The film sync.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="imdbId">The IMDb id, if known.</param>
    /// <param name="type">The media type.</param>
    /// <returns>The library film or show, or null if there is none.</returns>
    public static BaseItem? FindOwnedTitle(this IDiscoveryFilmSync sync, int tmdbId, string? imdbId, DiscoveryMediaType type)
    {
        ArgumentNullException.ThrowIfNull(sync);
        return type == DiscoveryMediaType.Series ? sync.FindOwnedSeries(tmdbId, imdbId) : sync.FindOwned(tmdbId, imdbId);
    }
}
