using System;
using System.Text.Json.Nodes;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// One row of a home row layout. The order of a row is its position in the layout.
/// </summary>
/// <param name="Id">The stable row id.</param>
/// <param name="Provider">The provider id, see <see cref="HomeRowProviderIds"/>.</param>
/// <param name="Title">The optional custom title.</param>
/// <param name="Config">The provider configuration.</param>
/// <param name="Enabled">Whether the row is shown.</param>
public sealed record HomeRowInstance(Guid Id, string Provider, string? Title, JsonObject Config, bool Enabled);
