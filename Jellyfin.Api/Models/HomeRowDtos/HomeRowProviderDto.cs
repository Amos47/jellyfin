using System.Text.Json.Nodes;

namespace Jellyfin.Api.Models.HomeRowDtos;

/// <summary>
/// A home row provider.
/// </summary>
/// <param name="Id">The provider id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Schema">The JSON schema of the provider settings, with options computed for the caller.</param>
/// <param name="Defaults">The default settings.</param>
public sealed record HomeRowProviderDto(string Id, string Name, string Description, JsonObject Schema, JsonObject Defaults);
