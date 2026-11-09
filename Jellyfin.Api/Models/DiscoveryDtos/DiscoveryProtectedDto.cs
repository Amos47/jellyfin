using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The titles the discovery app must never delete.
/// </summary>
/// <param name="TmdbIds">
/// The TMDB ids of the films (movies only) saved in anyone's list or with a download request that isn't done, each
/// once, ascending: kept for apps that know no media types.
/// </param>
/// <param name="Items">The same, for every media type: films first, then shows, each ascending by TMDB id.</param>
public sealed record DiscoveryProtectedDto(IReadOnlyList<int> TmdbIds, IReadOnlyList<DiscoveryProtectedItemDto> Items);
