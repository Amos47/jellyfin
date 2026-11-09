namespace Jellyfin.Api.Models.DiscoveryDtos;

/// <summary>
/// Whether the discovery app can carry out downloads, per media type.
/// </summary>
/// <param name="Enabled">Films: true only when the app's download configuration (Radarr) is complete.</param>
/// <param name="SeriesEnabled">Shows: true only when the app's show download configuration (Sonarr) is complete. Optional on a PUT, where a missing value means false, so an app that knows no shows turns show downloads off.</param>
public sealed record DiscoveryDownloadsSettingDto(bool? Enabled, bool? SeriesEnabled = null);
