using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Adds extra data to full system backups and restores it again.
/// </summary>
public interface IBackupContributor
{
    /// <summary>
    /// Writes the contributor's data into the backup archive.
    /// </summary>
    /// <param name="archive">The archive being created.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the data is written.</returns>
    Task WriteAsync(ZipArchive archive, CancellationToken cancellationToken);

    /// <summary>
    /// Restores the contributor's data from the backup archive.
    /// </summary>
    /// <param name="archive">The archive being restored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the data is restored.</returns>
    Task RestoreAsync(ZipArchive archive, CancellationToken cancellationToken);
}
