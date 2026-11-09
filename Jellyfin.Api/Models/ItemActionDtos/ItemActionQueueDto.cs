using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Models.ItemActionDtos;

/// <summary>
/// The items a play-queue action plays.
/// </summary>
/// <param name="ItemIds">The item ids, in play order.</param>
/// <param name="Items">The same items, in the same order, with the names a player shows.</param>
public sealed record ItemActionQueueDto(IReadOnlyList<Guid> ItemIds, IReadOnlyList<ItemActionQueueItemDto> Items);
