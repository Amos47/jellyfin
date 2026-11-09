namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The progress the discovery app reports for a download request.
/// </summary>
/// <param name="State">The new state: downloading or failed.</param>
public sealed record DiscoveryRequestUpdateDto(string? State);
