using System;
using System.Text.Json.Nodes;

namespace Jellyfin.Api.Models.HomeRowDtos;

/// <summary>
/// One row of a home row layout.
/// </summary>
/// <param name="Id">The row id. An empty id gets a new id when saved.</param>
/// <param name="Provider">The provider id.</param>
/// <param name="Title">The optional custom title.</param>
/// <param name="Config">The provider settings.</param>
/// <param name="Enabled">Whether the row is shown.</param>
/// <param name="IsAvailable">Whether the row's provider is registered. Ignored on input.</param>
public sealed record HomeRowInstanceDto(Guid Id, string Provider, string? Title, JsonObject Config, bool Enabled, bool IsAvailable);
