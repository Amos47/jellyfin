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
/// <param name="ReadyFilms">
/// The films the caller can play now: discovery films with a local trailer, plus films owned in their library.
/// </param>
/// <param name="Ready">
/// Whether <paramref name="ReadyFilms"/> reaches the default minimum (5), so a Movie discovery row with default
/// settings can pick the list.
/// </param>
public sealed record DiscoveryListDto(string Id, string Title, string Source, IReadOnlyList<DiscoveryFilmDto> Films, string? Group, int ReadyFilms, bool Ready);
