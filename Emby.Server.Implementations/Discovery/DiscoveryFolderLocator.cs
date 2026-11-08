using System;
using System.IO;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: creates and registers the discovery folder at each boot (like the playlists folder) and hands it out.
/// </summary>
public class DiscoveryFolderLocator : IDiscoveryFolderLocator
{
    private volatile DiscoveryFolder? _folder;

    /// <inheritdoc />
    public Guid FolderId => _folder?.Id ?? Guid.Empty;

    /// <inheritdoc />
    public BasePluginFolder GetFolder()
        => _folder ?? throw new InvalidOperationException("The discovery folder has not been registered yet.");

    /// <summary>
    /// Creates or loads the discovery folder and registers it under the root folder.
    /// </summary>
    /// <param name="rootFolder">The root folder.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="paths">The application paths.</param>
    public void Register(AggregateFolder rootFolder, ILibraryManager libraryManager, IServerApplicationPaths paths)
    {
        var path = Path.Combine(paths.DataPath, "discovery");
        var info = Directory.CreateDirectory(path);

        var folder = new DiscoveryFolder
        {
            Path = path,
            DateCreated = info.CreationTimeUtc,
            DateModified = info.LastWriteTimeUtc,
        };

        folder.Id = libraryManager.GetNewItemId(folder.Path, folder.GetType());

        if (libraryManager.GetItemById(folder.Id) is DiscoveryFolder dbItem
            && string.Equals(dbItem.Path, folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            folder = dbItem;
        }

        if (!folder.ParentId.Equals(rootFolder.Id))
        {
            rootFolder.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).GetAwaiter().GetResult();
            folder.ParentId = rootFolder.Id;
            folder.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).GetAwaiter().GetResult();
        }

        rootFolder.AddVirtualChild(folder);
        libraryManager.RegisterItem(folder);

        _folder = folder;
    }
}
