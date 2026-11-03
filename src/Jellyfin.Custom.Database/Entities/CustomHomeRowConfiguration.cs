using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A user's customized home row layout.
/// </summary>
public class CustomHomeRowConfiguration
{
    /// <summary>
    /// Gets or sets the id of the user owning the layout.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the serialized rows of the layout.
    /// </summary>
    public required string RowsJson { get; set; }

    /// <summary>
    /// Gets or sets the date the layout was last modified.
    /// </summary>
    public DateTime DateModified { get; set; }
}
