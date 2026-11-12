namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// The outcome the discovery app reports for a season request.
/// </summary>
/// <param name="State">The new state: handled or failed.</param>
/// <param name="Reason">Optional: why it failed.</param>
public sealed record DiscoverySeasonRequestUpdateDto(string? State, string? Reason = null);
