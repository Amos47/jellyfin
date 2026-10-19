using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Locates the fork's own database file and creates it once as a file-level copy of the official database.
/// The official database is only ever read as a file and is never opened or written.
/// </summary>
public static class ForkedDatabase
{
    /// <summary>
    /// The file name of the fork's live database.
    /// </summary>
    public const string FileName = "custom-jelly.db";

    /// <summary>
    /// The file name of the official database the fork is copied from.
    /// </summary>
    public const string SourceFileName = "jellyfin.db";

    /// <summary>
    /// The file name of the marker that records the fork copy has been made.
    /// </summary>
    public const string MarkerFileName = "custom-jelly.forked";

    /// <summary>
    /// Gets the path of the fork's live database.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <returns>The combined path.</returns>
    public static string GetPath(string dataPath) => Path.Combine(dataPath, FileName);

    /// <summary>
    /// Ensures the fork's database file exists, copying the official database once if needed.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <param name="logger">A logger.</param>
    /// <returns>The path of the fork's database file.</returns>
    public static string Prepare(string dataPath, ILogger logger)
    {
        var forkPath = GetPath(dataPath);
        var sourcePath = Path.Combine(dataPath, SourceFileName);
        var markerPath = Path.Combine(dataPath, MarkerFileName);

        if (File.Exists(forkPath))
        {
            if (!File.Exists(markerPath))
            {
                WriteMarker(markerPath);
            }

            return forkPath;
        }

        if (File.Exists(markerPath))
        {
            // Never re-copy the frozen database: that would roll back history to the fork day.
            logger.LogWarning(
                "{Fork} is missing although it was created before; not copying {Source} again",
                FileName,
                SourceFileName);
            return forkPath;
        }

        if (!File.Exists(sourcePath))
        {
            return forkPath;
        }

        var tempPath = forkPath + ".tmp";
        var tempWal = tempPath + "-wal";
        var sourceWal = sourcePath + "-wal";

        // The fork database is known to be absent here, so any sidecar next to it is stale and must not be
        // replayed onto the new copy.
        foreach (var stale in new[] { forkPath + "-wal", forkPath + "-shm", tempPath, tempWal })
        {
            File.Delete(stale);
        }

        File.Copy(sourcePath, tempPath, true);
        if (File.Exists(sourceWal))
        {
            File.Copy(sourceWal, tempWal, true);
            File.Move(tempWal, forkPath + "-wal", true);
        }

        File.Move(tempPath, forkPath, false);
        WriteMarker(markerPath);
        logger.LogInformation("Created {Fork} as a copy of {Source}", FileName, SourceFileName);
        return forkPath;
    }

    private static void WriteMarker(string markerPath)
        => File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
}
