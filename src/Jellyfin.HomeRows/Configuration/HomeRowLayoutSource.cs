namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// Where an effective home row layout comes from.
/// </summary>
public enum HomeRowLayoutSource
{
    /// <summary>
    /// The user's own saved layout.
    /// </summary>
    User,

    /// <summary>
    /// The server wide default saved by an admin.
    /// </summary>
    AdminDefault,

    /// <summary>
    /// The user's upstream home sections, mapped to providers.
    /// </summary>
    UpstreamSections,

    /// <summary>
    /// Upstream's standard home screen order.
    /// </summary>
    StandardDefault
}
