using System;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: finds the hidden folder that holds the virtual films of the movie discovery feature.
/// </summary>
public interface IDiscoveryFolderLocator
{
    /// <summary>
    /// Gets the id of the discovery folder. Empty until the library has registered the folder at startup.
    /// </summary>
    Guid FolderId { get; }

    /// <summary>
    /// Gets the discovery folder (an Emby.Server.Implementations DiscoveryFolder).
    /// </summary>
    /// <returns>The folder.</returns>
    /// <exception cref="InvalidOperationException">The folder has not been registered yet.</exception>
    BasePluginFolder GetFolder();
}
