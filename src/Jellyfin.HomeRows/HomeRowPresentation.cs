using MediaBrowser.Model.Entities;

namespace Jellyfin.HomeRows;

/// <summary>
/// The shape of a home row card.
/// </summary>
public enum HomeRowCardShape
{
    /// <summary>Poster.</summary>
    Poster,

    /// <summary>Thumb.</summary>
    Thumb,

    /// <summary>Square.</summary>
    Square,

    /// <summary>Button.</summary>
    Button
}

/// <summary>
/// The size of a home row card.
/// </summary>
public enum HomeRowCardSize
{
    /// <summary>Small.</summary>
    Small,

    /// <summary>Medium.</summary>
    Medium,

    /// <summary>Large.</summary>
    Large
}

/// <summary>
/// How a home row is displayed.
/// </summary>
/// <param name="CardShape">The card shape.</param>
/// <param name="PreferredImageType">The preferred image type.</param>
/// <param name="FallbackImageType">The image type used when the preferred one is missing.</param>
/// <param name="Size">The card size.</param>
public sealed record HomeRowPresentation(HomeRowCardShape CardShape, ImageType PreferredImageType, ImageType? FallbackImageType = null, HomeRowCardSize? Size = null)
{
    /// <summary>
    /// Gets the id of the discovery list whose page the row opens (<c>GET Discovery/Lists/{listId}/Page</c>), or null
    /// when the row has no list page. Fork: clients open it from the row title and See all.
    /// </summary>
    public string? PageListId { get; init; }
}
