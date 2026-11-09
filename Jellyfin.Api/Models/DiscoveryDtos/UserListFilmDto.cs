using System;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A film in a user's list.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id, if known.</param>
/// <param name="AddedAt">When the film was added (UTC).</param>
/// <param name="Type">The media type of the TMDB id: <c>Movie</c> or <c>Series</c>. A user list holds both.</param>
public sealed record UserListFilmDto(int TmdbId, string? ImdbId, DateTime AddedAt, DiscoveryMediaType Type);
