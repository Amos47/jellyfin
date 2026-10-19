using System.Collections.Generic;

namespace Jellyfin.HomeRows;

/// <summary>
/// Resolves home row providers by id.
/// </summary>
public interface IHomeRowProviderFactory
{
    /// <summary>
    /// Gets all providers in registration order.
    /// </summary>
    IReadOnlyList<IHomeRowProvider> Providers { get; }

    /// <summary>
    /// Gets a provider by its case-sensitive id.
    /// </summary>
    /// <param name="id">The provider id.</param>
    /// <returns>The provider, or <c>null</c> if none has that id.</returns>
    IHomeRowProvider? Get(string id);
}
