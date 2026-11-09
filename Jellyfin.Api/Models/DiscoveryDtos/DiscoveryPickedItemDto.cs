using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A title the home screens pick on a day.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="Type">The media type of the TMDB id: <c>Movie</c> or <c>Series</c>.</param>
public sealed record DiscoveryPickedItemDto(int TmdbId, DiscoveryMediaType Type);
