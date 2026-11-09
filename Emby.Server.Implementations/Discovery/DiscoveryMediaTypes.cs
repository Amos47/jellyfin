using System;
using MediaBrowser.Controller.Discovery;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: maps <see cref="DiscoveryMediaType"/> to the name the fork database stores, and back.
/// </summary>
internal static class DiscoveryMediaTypes
{
    /// <summary>
    /// Gets the stored name of a type.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>Its name.</returns>
    public static string ToStored(DiscoveryMediaType type)
        => type switch
        {
            DiscoveryMediaType.Movie => nameof(DiscoveryMediaType.Movie),
            DiscoveryMediaType.Series => nameof(DiscoveryMediaType.Series),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown media type."),
        };

    /// <summary>
    /// Gets the type of a stored name.
    /// </summary>
    /// <param name="stored">The stored name.</param>
    /// <returns>The type.</returns>
    public static DiscoveryMediaType FromStored(string stored)
        => stored switch
        {
            nameof(DiscoveryMediaType.Movie) => DiscoveryMediaType.Movie,
            nameof(DiscoveryMediaType.Series) => DiscoveryMediaType.Series,
            _ => throw new ArgumentOutOfRangeException(nameof(stored), stored, "Unknown stored media type."),
        };
}
