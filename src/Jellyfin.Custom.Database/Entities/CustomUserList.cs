using System;
using System.Collections.Generic;

namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// A user's named list of films, such as their default "Saved" list.
/// </summary>
public class CustomUserList
{
    /// <summary>
    /// Gets or sets the list id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the id of the user owning the list.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the list name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is the user's default list (Saved). A user has at most one.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets the date the list was created.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets the films of the list.
    /// </summary>
    public ICollection<CustomUserListItem> Items { get; } = new List<CustomUserListItem>();
}
