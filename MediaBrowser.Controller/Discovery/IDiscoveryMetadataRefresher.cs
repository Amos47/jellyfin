using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: queues the metadata and image refresh of a new discovery film, and the probe of a pushed trailer file.
/// Swapped for a no-op in tests so they make no network calls and nothing refreshes behind their back.
/// </summary>
public interface IDiscoveryMetadataRefresher
{
    /// <summary>
    /// Queues a full metadata and image refresh of an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    void QueueRefresh(Guid itemId);

    /// <summary>
    /// Queues a default refresh of a pushed trailer item, which probes its file for media info.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    void QueueProbe(Guid itemId);
}
