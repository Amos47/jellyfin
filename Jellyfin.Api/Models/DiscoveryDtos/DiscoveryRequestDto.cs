using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A download request of a discovery film.
/// </summary>
/// <param name="TmdbId">The TMDB id of the film.</param>
/// <param name="State">The state: requested, downloading, failed or done.</param>
/// <param name="UpdatedAt">When the request last changed (UTC).</param>
/// <param name="UserIds">The users who asked for the film.</param>
/// <param name="Type">The media type of the TMDB id: <c>Movie</c> or <c>Series</c>.</param>
public sealed record DiscoveryRequestDto(int TmdbId, string State, DateTime UpdatedAt, IReadOnlyList<Guid> UserIds, DiscoveryMediaType Type);
