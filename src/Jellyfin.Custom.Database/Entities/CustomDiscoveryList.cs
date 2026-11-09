using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A list of films pushed to the discovery hub by an outside app.
/// </summary>
public class CustomDiscoveryList
{
    /// <summary>
    /// Gets or sets the id of the list, at most 64 characters of <c>[a-z0-9-]</c>.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the title of the list.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Gets or sets where the list came from.
    /// </summary>
    public required string Source { get; set; }

    /// <summary>
    /// Gets or sets the ordered films, serialized as <c>[{tmdbId, imdbId}]</c>.
    /// </summary>
    public required string FilmsJson { get; set; }

    /// <summary>
    /// Gets or sets the group of the list, at most 100 characters, or null for none.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Gets or sets the media type of the list's films: <c>Movie</c> or <c>Series</c>. Lists from before shows are
    /// <c>Movie</c>.
    /// </summary>
    public string ItemType { get; set; } = CustomMediaTypes.Movie;

    /// <summary>
    /// Gets or sets the date the list was last modified.
    /// </summary>
    public DateTime DateModified { get; set; }
}
