using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// Every season Sonarr has a status for, as the discovery app reports it: a full replacement.
/// </summary>
/// <param name="Seasons">The seasons.</param>
public sealed record DiscoverySonarrSeasonsDto(IReadOnlyList<DiscoverySonarrSeasonDto>? Seasons);
