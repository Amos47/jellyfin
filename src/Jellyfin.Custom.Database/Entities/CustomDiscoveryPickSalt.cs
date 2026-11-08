using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// The salt of a day's re-picked discovery lists: an admin asked for new picks that day.
/// </summary>
public class CustomDiscoveryPickSalt
{
    /// <summary>
    /// Gets or sets the day (server local).
    /// </summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// Gets or sets the salt.
    /// </summary>
    public required string Salt { get; set; }

    /// <summary>
    /// Gets or sets the date the salt was set.
    /// </summary>
    public DateTime DateCreated { get; set; }
}
