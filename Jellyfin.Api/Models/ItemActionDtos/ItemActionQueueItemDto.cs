using System;

namespace Jellyfin.Api.Models.ItemActionDtos;

/// <summary>
/// One item of a play queue.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Title">The title to show, or null to use the item's own name.</param>
/// <param name="Subtitle">The line under the title, or null.</param>
/// <param name="FilmId">
/// The film the item belongs to: for a trailer, the user's owned film when they own it, else the discovery copy. Null
/// when unknown. Fork: a player closes onto this film's page.
/// </param>
public sealed record ItemActionQueueItemDto(Guid Id, string? Title, string? Subtitle, Guid? FilmId);
