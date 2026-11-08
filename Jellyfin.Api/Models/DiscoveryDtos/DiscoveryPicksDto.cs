using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The films the home screens pick on a day.
/// </summary>
/// <param name="Date">The day.</param>
/// <param name="TmdbIds">The TMDB ids of the films in every list picked for any user, each once.</param>
public sealed record DiscoveryPicksDto(DateOnly Date, IReadOnlyList<int> TmdbIds);
