using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A discovery list to save.
/// </summary>
/// <param name="Title">The title.</param>
/// <param name="Source">The app the list comes from.</param>
/// <param name="Films">The films, in order. A TMDB id given twice is kept once, at its first place.</param>
public sealed record DiscoveryListUpdateDto(string Title, string Source, IReadOnlyList<DiscoveryFilmDto> Films);
