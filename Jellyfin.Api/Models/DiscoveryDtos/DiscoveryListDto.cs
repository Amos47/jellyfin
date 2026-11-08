using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A discovery list.
/// </summary>
/// <param name="Id">The list id.</param>
/// <param name="Title">The title.</param>
/// <param name="Source">The app the list came from.</param>
/// <param name="Films">The films, in order.</param>
/// <param name="Group">The group of the list, or null for none.</param>
public sealed record DiscoveryListDto(string Id, string Title, string Source, IReadOnlyList<DiscoveryFilmDto> Films, string? Group);
