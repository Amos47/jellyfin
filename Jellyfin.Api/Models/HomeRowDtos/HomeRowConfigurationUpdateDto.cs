using System.Collections.Generic;

namespace Jellyfin.Api.Models.HomeRowDtos;

/// <summary>
/// A home row layout to save.
/// </summary>
/// <param name="Rows">The rows, in display order.</param>
public sealed record HomeRowConfigurationUpdateDto(IReadOnlyList<HomeRowInstanceDto> Rows);
