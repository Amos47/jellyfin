using System;
using System.Collections.Generic;
using Jellyfin.HomeRows;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Api.Models.HomeRowDtos;

/// <summary>
/// The items of one home row.
/// </summary>
/// <param name="RowId">The row id.</param>
/// <param name="Title">The row title, or null to use the provider name.</param>
/// <param name="Items">The items.</param>
/// <param name="TotalRecordCount">The total number of items, if known.</param>
/// <param name="StartIndex">The index of the first item.</param>
/// <param name="Presentation">How the items should be displayed.</param>
/// <param name="Error">The error, "needs-configuration" or null.</param>
public sealed record HomeRowItemsDto(
    Guid RowId,
    string? Title,
    IReadOnlyList<BaseItemDto> Items,
    int? TotalRecordCount,
    int StartIndex,
    HomeRowPresentation Presentation,
    string? Error);
