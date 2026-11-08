using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;

namespace Jellyfin.HomeRows;

/// <summary>
/// The context a home row provider runs in.
/// </summary>
/// <param name="User">The user the row is for.</param>
/// <param name="DtoOptions">The options used to build item DTOs.</param>
/// <param name="UtcNow">The current UTC time.</param>
public sealed record HomeRowContext(User User, DtoOptions DtoOptions, DateTime UtcNow)
{
    /// <summary>
    /// Gets the key of the row an <see cref="IExpandingHomeRowProvider"/> row expanded into that is asked for
    /// (<see cref="HomeSubRow.Key"/>), or <c>null</c> when the parent row itself is asked for. Always <c>null</c> for
    /// other providers.
    /// </summary>
    public string? SubRowKey { get; init; }
}
