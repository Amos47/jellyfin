using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// Records that a one-time data change of the fork ran, so it never runs again.
/// </summary>
public class CustomMarker
{
    /// <summary>
    /// Gets or sets the marker id, naming the change.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the date the change ran (UTC).
    /// </summary>
    public DateTime DateApplied { get; set; }
}
