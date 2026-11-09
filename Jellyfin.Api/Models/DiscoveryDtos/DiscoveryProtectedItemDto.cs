using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A title the discovery app must never delete.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="Type">The media type of the TMDB id: <c>Movie</c> or <c>Series</c>.</param>
public sealed record DiscoveryProtectedItemDto(int TmdbId, DiscoveryMediaType Type);
