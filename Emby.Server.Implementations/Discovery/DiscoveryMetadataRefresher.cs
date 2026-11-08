using System;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: queues a full metadata and image refresh through the provider manager, so the built-in TMDB provider fills
/// in a new discovery film.
/// </summary>
public class DiscoveryMetadataRefresher : IDiscoveryMetadataRefresher
{
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryMetadataRefresher"/> class.
    /// </summary>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    public DiscoveryMetadataRefresher(IProviderManager providerManager, IFileSystem fileSystem)
    {
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public void QueueRefresh(Guid itemId)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = true,
        };
        _providerManager.QueueRefresh(itemId, options, RefreshPriority.Normal);
    }
}
