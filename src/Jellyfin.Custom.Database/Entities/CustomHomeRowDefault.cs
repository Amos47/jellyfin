using System;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// The server wide default home row layout. There is only ever one row, with <see cref="Id"/> 1.
/// </summary>
public class CustomHomeRowDefault
{
    /// <summary>
    /// Gets or sets the id of the row, always 1.
    /// </summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// Gets or sets the serialized rows of the default layout.
    /// </summary>
    public required string RowsJson { get; set; }

    /// <summary>
    /// Gets or sets the date the default was last modified.
    /// </summary>
    public DateTime DateModified { get; set; }

    /// <summary>
    /// Gets or sets the id of the user who last modified the default.
    /// </summary>
    public Guid ModifiedByUserId { get; set; }
}
