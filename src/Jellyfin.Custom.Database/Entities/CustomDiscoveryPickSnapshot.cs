using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A user's saved discovery picks of a day: the first pick of the day is kept, so the home screen doesn't change as
/// lists become ready.
/// </summary>
public class CustomDiscoveryPickSnapshot
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the day (server local).
    /// </summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// Gets or sets the picked list ids in display order, as a JSON array of strings.
    /// </summary>
    public required string ListIdsJson { get; set; }
}
