using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The films the discovery app must never delete.
/// </summary>
/// <param name="TmdbIds">The TMDB ids of the films saved in anyone's list or with a download request that isn't done, each once.</param>
public sealed record DiscoveryProtectedDto(IReadOnlyList<int> TmdbIds);
