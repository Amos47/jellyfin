using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// One of the current user's named lists, such as Saved.
/// </summary>
/// <param name="Id">The list id.</param>
/// <param name="Name">The list name.</param>
/// <param name="IsDefault">Whether this is the user's default list (Saved), which the Save action uses.</param>
/// <param name="PageListId">The id of the list's page, for <c>GET Discovery/Lists/{listId}/Page</c>: <c>user:{id}</c>.</param>
/// <param name="Films">The films, newest first.</param>
public sealed record UserListDto(Guid Id, string Name, bool IsDefault, string PageListId, IReadOnlyList<UserListFilmDto> Films);
