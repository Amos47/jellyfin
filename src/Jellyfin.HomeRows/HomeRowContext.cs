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
    /// Gets which of the rows an <see cref="IExpandingHomeRowProvider"/> row expanded into is asked for, from 0.
    /// Always 0 for other providers.
    /// </summary>
    public int SubRow { get; init; }
}
