namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// What Sonarr is doing with one season of a show.
/// </summary>
/// <param name="SeriesTmdbId">The TMDB id of the series.</param>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="Status">The status: unmonitored, wanted, downloading or idle.</param>
/// <param name="Mismatch">Whether Sonarr's episode count for the season differs a lot from TMDB's.</param>
public sealed record DiscoverySonarrSeasonDto(int SeriesTmdbId, int SeasonNumber, string? Status, bool Mismatch);
