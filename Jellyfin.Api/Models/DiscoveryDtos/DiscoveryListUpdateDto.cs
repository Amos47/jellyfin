using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A discovery list to save.
/// </summary>
/// <param name="Title">The title.</param>
/// <param name="Source">The app the list comes from.</param>
/// <param name="Films">The films, in order. A TMDB id given twice is kept once, at its first place.</param>
/// <param name="Group">
/// Optional: the group of the list, at most 100 characters (trimmed); blank or missing means none. A user's rows show
/// at most one list of a group a day; a list without a group is a group of its own.
/// </param>
public sealed record DiscoveryListUpdateDto(string Title, string Source, IReadOnlyList<DiscoveryFilmDto> Films, string? Group = null);
