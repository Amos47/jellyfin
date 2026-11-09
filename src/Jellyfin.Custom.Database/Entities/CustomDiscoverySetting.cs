namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A server-wide discovery setting that the discovery app sets, such as whether downloads are enabled.
/// </summary>
public class CustomDiscoverySetting
{
    /// <summary>
    /// Gets or sets the setting key.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Gets or sets the value.
    /// </summary>
    public required string Value { get; set; }
}
