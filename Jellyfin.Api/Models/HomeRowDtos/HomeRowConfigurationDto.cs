using System.Collections.Generic;
using Jellyfin.HomeRows.Configuration;

namespace Jellyfin.Api.Models.HomeRowDtos;

/// <summary>
/// The home row layout that applies to the caller.
/// </summary>
/// <param name="ApiVersion">The version of the home rows API.</param>
/// <param name="Source">Where the layout comes from.</param>
/// <param name="IsCustomized">Whether the caller saved their own layout.</param>
/// <param name="Rows">The rows, in display order.</param>
public sealed record HomeRowConfigurationDto(int ApiVersion, HomeRowLayoutSource Source, bool IsCustomized, IReadOnlyList<HomeRowInstanceDto> Rows);
