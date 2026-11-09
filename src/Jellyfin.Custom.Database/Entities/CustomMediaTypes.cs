namespace Jellyfin.Custom.Database.Entities;

/// <summary>
/// The stored media types of the TMDB-keyed tables: the names of the server's <c>DiscoveryMediaType</c> values.
/// </summary>
public static class CustomMediaTypes
{
    /// <summary>
    /// The longest stored media type.
    /// </summary>
    public const int MaxLength = 16;

    /// <summary>
    /// A film: the type of every row from before shows.
    /// </summary>
    public const string Movie = "Movie";
}
