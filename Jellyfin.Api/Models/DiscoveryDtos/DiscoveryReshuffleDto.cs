using System;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A re-picked day.
/// </summary>
/// <param name="Date">The day (server local).</param>
/// <param name="Salt">The day's new salt.</param>
public sealed record DiscoveryReshuffleDto(DateOnly Date, string Salt);
