using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: queues the metadata and image refresh of a new discovery film. Swapped for a no-op in tests so they make
/// no network calls.
/// </summary>
public interface IDiscoveryMetadataRefresher
{
    /// <summary>
    /// Queues a full metadata and image refresh of an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    void QueueRefresh(Guid itemId);
}
