using System;
using System.IO;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests;

public sealed class ForkedDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forked-db-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger> _logger = new();

    public ForkedDatabaseTests()
    {
        Directory.CreateDirectory(_dir);
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    private string Source => Path.Combine(_dir, ForkedDatabase.SourceFileName);

    private string Fork => Path.Combine(_dir, ForkedDatabase.FileName);

    private string Marker => Path.Combine(_dir, ForkedDatabase.MarkerFileName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Prepare_NoFiles_ReturnsForkPathAndCreatesNothing()
    {
        var result = ForkedDatabase.Prepare(_dir, _logger.Object);

        Assert.Equal(Fork, result);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Prepare_SourceOnly_CopiesSourceAndWritesMarker()
    {
        CreateDb(Source, false);
        var bytes = File.ReadAllBytes(Source);
        var written = File.GetLastWriteTimeUtc(Source);

        var result = ForkedDatabase.Prepare(_dir, _logger.Object);

        Assert.Equal(Fork, result);
        Assert.Equal(1, CountRows(Fork));
        Assert.True(File.Exists(Marker));
        Assert.Equal(bytes, File.ReadAllBytes(Source));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Source));
        Assert.False(File.Exists(Fork + ".tmp"));
    }

    [Fact]
    public void Prepare_SourceWithWal_CopyContainsWalRows()
    {
        using var connection = CreateDb(Source, true);
        Assert.True(File.Exists(Source + "-wal"));
        Assert.True(new FileInfo(Source + "-wal").Length > 0);

        ForkedDatabase.Prepare(_dir, _logger.Object);

        Assert.Equal(1, CountRows(Fork));
    }

    [Fact]
    public void Prepare_ForkExists_DoesNothingButWriteMarker()
    {
        File.WriteAllText(Fork, "existing");
        CreateDb(Source, false);

        ForkedDatabase.Prepare(_dir, _logger.Object);

        Assert.Equal("existing", File.ReadAllText(Fork));
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public void Prepare_MarkerPresentForkMissing_DoesNotCopy()
    {
        File.WriteAllText(Marker, "x");
        CreateDb(Source, false);

        var result = ForkedDatabase.Prepare(_dir, _logger.Object);

        Assert.Equal(Fork, result);
        Assert.False(File.Exists(Fork));
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Prepare_StaleForkWalAndTempFiles_AreRemovedBeforeCopy()
    {
        var other = Path.Combine(_dir, "other.db");
        using (var otherConnection = CreateDb(other, true))
        {
            File.Copy(other + "-wal", Fork + "-wal");
        }

        File.WriteAllText(Fork + "-shm", "stale");
        File.WriteAllText(Fork + ".tmp", "stale");
        File.WriteAllText(Fork + ".tmp-wal", "stale");
        CreateDb(Source, false);

        ForkedDatabase.Prepare(_dir, _logger.Object);

        Assert.False(File.Exists(Fork + "-wal"));
        Assert.False(File.Exists(Fork + "-shm"));
        Assert.False(File.Exists(Fork + ".tmp"));
        Assert.False(File.Exists(Fork + ".tmp-wal"));
        Assert.Equal(1, CountRows(Fork));
    }

    private static SqliteConnection CreateDb(string path, bool wal)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = (wal ? "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" : string.Empty)
            + "CREATE TABLE T (V INTEGER); INSERT INTO T VALUES (1);";
        command.ExecuteNonQuery();
        if (wal)
        {
            return connection;
        }

        connection.Dispose();
        return connection;
    }

    private static long CountRows(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM T";
        return (long)command.ExecuteScalar()!;
    }
}
