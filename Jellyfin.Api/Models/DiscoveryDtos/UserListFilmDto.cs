using System;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A film in a user's list.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
/// <param name="AddedAt">When the film was added (UTC).</param>
public sealed record UserListFilmDto(int TmdbId, string? ImdbId, DateTime AddedAt);
