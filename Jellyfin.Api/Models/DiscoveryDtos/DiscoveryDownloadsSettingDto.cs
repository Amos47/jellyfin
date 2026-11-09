namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// Whether the discovery app can carry out downloads.
/// </summary>
/// <param name="Enabled">True only when the app's download configuration is complete.</param>
public sealed record DiscoveryDownloadsSettingDto(bool? Enabled);
