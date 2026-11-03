using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.HomeRows;

/// <summary>
/// The result of a home row request.
/// </summary>
/// <param name="Items">The items.</param>
/// <param name="TotalRecordCount">The total number of items, if known.</param>
/// <param name="Presentation">How the items should be displayed.</param>
public sealed record HomeRowResult(IReadOnlyList<BaseItemDto> Items, int? TotalRecordCount, HomeRowPresentation Presentation)
{
    /// <summary>
    /// Gets the title override.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the error, "needs-configuration" or null.
    /// </summary>
    public string? Error { get; init; }
}
