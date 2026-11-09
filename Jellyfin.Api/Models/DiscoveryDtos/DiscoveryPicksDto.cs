using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The titles of one media type the home screens pick on a day.
/// </summary>
/// <param name="Date">The day.</param>
/// <param name="TmdbIds">The TMDB ids of the titles in every list of the type picked for any user, each once.</param>
/// <param name="Type">The media type of the picks: <c>Movie</c> (the default) or <c>Series</c>.</param>
/// <param name="Items">The same titles, each with its type.</param>
public sealed record DiscoveryPicksDto(
    DateOnly Date,
    IReadOnlyList<int> TmdbIds,
    DiscoveryMediaType Type = DiscoveryMediaType.Movie,
    IReadOnlyList<DiscoveryPickedItemDto>? Items = null);
