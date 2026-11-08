using System;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A discovery film and what the server has for it.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ItemId">The id of the virtual film.</param>
/// <param name="Title">The film's title.</param>
/// <param name="HasTrailer">Whether the film has a local trailer.</param>
/// <param name="FirstShown">The first day it was shown on a home screen, if ever.</param>
/// <param name="LastShown">The last day it was shown on a home screen, if ever.</param>
public sealed record DiscoveryFilmStatusDto(int TmdbId, Guid ItemId, string Title, bool HasTrailer, DateOnly? FirstShown, DateOnly? LastShown);
