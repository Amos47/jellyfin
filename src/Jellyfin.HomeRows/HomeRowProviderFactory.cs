using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.HomeRows;

/// <inheritdoc />
public class HomeRowProviderFactory : IHomeRowProviderFactory
{
    private readonly Dictionary<string, IHomeRowProvider> _byId;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeRowProviderFactory"/> class.
    /// </summary>
    /// <param name="providers">The registered providers.</param>
    /// <exception cref="InvalidOperationException">Two providers share an id.</exception>
    public HomeRowProviderFactory(IEnumerable<IHomeRowProvider> providers)
    {
        Providers = providers.ToArray();
        _byId = new Dictionary<string, IHomeRowProvider>(Providers.Count, StringComparer.Ordinal);
        foreach (var provider in Providers)
        {
            if (!_byId.TryAdd(provider.Id, provider))
            {
                throw new InvalidOperationException($"Duplicate home row provider id '{provider.Id}'.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IHomeRowProvider> Providers { get; }

    /// <inheritdoc />
    public IHomeRowProvider? Get(string id) => _byId.GetValueOrDefault(id);
}
