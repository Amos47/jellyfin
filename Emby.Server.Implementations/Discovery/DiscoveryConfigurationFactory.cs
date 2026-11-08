using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: a configuration factory for <see cref="DiscoveryOptions"/>, picked up by the application host's
/// <see cref="IConfigurationFactory"/> scan like the branding one.
/// </summary>
public class DiscoveryConfigurationFactory : IConfigurationFactory
{
    /// <summary>
    /// The key of the discovery configuration.
    /// </summary>
    public const string Key = "discovery";

    /// <inheritdoc />
    public IEnumerable<ConfigurationStore> GetConfigurations()
    {
        return new[]
        {
            new ConfigurationStore
            {
                ConfigurationType = typeof(DiscoveryOptions),
                Key = Key
            }
        };
    }
}
