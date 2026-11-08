namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A film in a discovery list.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
public sealed record DiscoveryFilmDto(int TmdbId, string? ImdbId);
