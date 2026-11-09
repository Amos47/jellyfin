using System;
using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// A discovery list's page: a header and the list's films.
/// </summary>
/// <param name="Id">The list id.</param>
/// <param name="Title">The list title.</param>
/// <param name="Overview">The text under the title, or null.</param>
/// <param name="BackdropItemId">The item whose backdrop is the header's, the first film that has one; or null.</param>
/// <param name="PrimaryItemId">The item whose poster is the header's, the first film that has one; or null.</param>
/// <param name="Items">The films the user can play (owned, or with a local trailer), in list order.</param>
public sealed record DiscoveryListPageDto(
    string Id,
    string Title,
    string? Overview,
    Guid? BackdropItemId,
    Guid? PrimaryItemId,
    IReadOnlyList<BaseItemDto> Items);
