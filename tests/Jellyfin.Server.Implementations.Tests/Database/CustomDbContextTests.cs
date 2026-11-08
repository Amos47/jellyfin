using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests;

public sealed class CustomDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public CustomDbContextTests()
    {
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Migrate_CreatesOnlyCustomTablesAndHistory()
    {
        var jellyfinOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using (var jellyfin = new JellyfinDbContext(
            jellyfinOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)))
        {
            await jellyfin.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var before = await GetTablesAsync();
        var historyRowsBefore = await CountHistoryRowsAsync();

        var customOptions = new DbContextOptionsBuilder<CustomDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
            .Options;
        using (var custom = new CustomDbContext(customOptions))
        {
            await custom.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var added = await GetTablesAsync();
        added.ExceptWith(before);

        // EF Core 9+ keeps a shared lock table next to the history tables; it is infrastructure, not fork schema.
        added.Remove("__EFMigrationsLock");

        Assert.Equal(
            new HashSet<string> { "CustomDiscoveryLists", "CustomDiscoveryShown", "CustomHomeRowConfigurations", "CustomHomeRowDefaults", "__CustomMigrationsHistory" },
            added);
        Assert.Equal(historyRowsBefore, await CountHistoryRowsAsync());
    }

    private async Task<HashSet<string>> GetTablesAsync()
    {
        var tables = new HashSet<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private async Task<long> CountHistoryRowsAsync()
    {
        if (!(await GetTablesAsync()).Contains("__EFMigrationsHistory"))
        {
            return 0;
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
