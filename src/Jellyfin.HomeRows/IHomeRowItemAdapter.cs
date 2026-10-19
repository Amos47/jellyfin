using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.HomeRows;

/// <summary>
/// Converts items to DTOs for home rows.
/// </summary>
public interface IHomeRowItemAdapter
{
    /// <summary>
    /// Converts items to DTOs for the context user.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <param name="context">The request context.</param>
    /// <param name="skipVisibilityCheck">Whether to skip the visibility check.</param>
    /// <returns>The DTOs.</returns>
    IReadOnlyList<BaseItemDto> ToDtos(IReadOnlyList<BaseItem> items, HomeRowContext context, bool skipVisibilityCheck = false);
}
