using System;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A request for new discovery picks.
/// </summary>
/// <param name="Date">Optional: the day (server local), as yyyy-MM-dd. Defaults to today.</param>
public sealed record DiscoveryReshuffleRequestDto(DateOnly? Date = null);
