using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Custom.Database;
using MediaBrowser.Controller.Discovery;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests;

/// <summary>
/// The show seasons migration only adds the season request and Sonarr season status tables. A database at the schema
/// before it, with rows in every table, must come out with every row kept.
/// </summary>
public sealed class CustomDbShowSeasonsMigrationTests : IDisposable
{
    // The last migration before the show seasons.
    private const string PreviousMigration = "20261109072032_AddDiscoveryMediaType";

    private static readonly Guid _userA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid _userB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid _savedA = Guid.Parse("01010101-0202-0303-0404-050505050505");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CustomDbContext> _options;

    public CustomDbShowSeasonsMigrationTests()
    {
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<CustomDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
            .Options;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Migration_AddsTables_OnSeededCurrentSchema()
    {
        await using (var context = new CustomDbContext(_options))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken: Ct);
        }

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('CustomSeasonRequests', 'CustomSonarrSeasonStatus')"));

        var day = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await ExecAsync($"INSERT INTO CustomHomeRowConfigurations (UserId, RowsJson, DateModified) VALUES ('{Text(_userA)}', '[]', '2026-11-08 10:00:00')");
        await ExecAsync($"INSERT INTO CustomHomeRowDefaults (Id, RowsJson, DateModified, ModifiedByUserId) VALUES (1, '[]', '2026-11-08 10:00:00', '{Text(_userA)}')");
        await ExecAsync(
            "INSERT INTO CustomDiscoveryLists (Id, Title, Source, FilmsJson, \"Group\", DateModified, ItemType) VALUES "
            + "('films', 'Films', 'app', '[{\"TmdbId\":30,\"ImdbId\":null}]', NULL, '2026-11-08 10:00:00', 'Movie'), "
            + "('shows', 'Shows', 'app', '[{\"TmdbId\":456,\"ImdbId\":null}]', NULL, '2026-11-08 10:00:00', 'Series')");
        await ExecAsync($"INSERT INTO CustomDiscoveryShown (TmdbId, MediaType, FirstShown, LastShown) VALUES (30, 'Movie', '{day}', '{day}'), (456, 'Series', '{day}', '{day}')");
        await ExecAsync($"INSERT INTO CustomDiscoveryPickSalts (Day, Salt, DateCreated) VALUES ('{day}', 'salt-1', '2026-11-08 09:00:00')");
        await ExecAsync($"INSERT INTO CustomDiscoveryPickSnapshots (UserId, Day, MediaType, ListIdsJson) VALUES ('{Text(_userA)}', '{day}', 'Movie', '[\"films\"]'), ('{Text(_userA)}', '{day}', 'Series', '[\"shows\"]')");
        await ExecAsync($"INSERT INTO CustomUserLists (Id, UserId, Name, IsDefault, DateCreated) VALUES ('{Text(_savedA)}', '{Text(_userA)}', 'Saved', 1, '2026-11-01 08:00:00')");
        await ExecAsync(
            $"INSERT INTO CustomUserListItems (ListId, TmdbId, MediaType, ImdbId, AddedAt) VALUES ('{Text(_savedA)}', 30, 'Movie', NULL, '2026-11-02 08:00:00'), "
            + $"('{Text(_savedA)}', 456, 'Series', NULL, '2026-11-03 08:00:00')");
        await ExecAsync("INSERT INTO CustomDiscoveryRequests (TmdbId, MediaType, State, UpdatedAt) VALUES (30, 'Movie', 'downloading', '2026-11-05 08:00:00'), (456, 'Series', 'requested', '2026-11-06 08:00:00')");
        await ExecAsync($"INSERT INTO CustomDiscoveryRequestUsers (TmdbId, MediaType, UserId) VALUES (30, 'Movie', '{Text(_userA)}'), (456, 'Series', '{Text(_userB)}')");
        await ExecAsync("INSERT INTO CustomDiscoverySettings (Key, Value) VALUES ('downloads-enabled', 'True'), ('downloads-enabled-series', 'True')");
        await ExecAsync("INSERT INTO CustomMarkers (Id, DateApplied) VALUES ('saved-row-default', '2026-11-08 12:00:00')");

        await using (var context = new CustomDbContext(_options))
        {
            await context.Database.MigrateAsync(Ct);
        }

        // Every row kept, and the foreign keys still hold.
        Assert.Equal(1L, await CountAsync("CustomHomeRowConfigurations"));
        Assert.Equal(1L, await CountAsync("CustomHomeRowDefaults"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryLists"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryShown"));
        Assert.Equal(1L, await CountAsync("CustomDiscoveryPickSalts"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryPickSnapshots"));
        Assert.Equal(1L, await CountAsync("CustomUserLists"));
        Assert.Equal(2L, await CountAsync("CustomUserListItems"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryRequests"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryRequestUsers"));
        Assert.Equal(2L, await CountAsync("CustomDiscoverySettings"));
        Assert.Equal(1L, await CountAsync("CustomMarkers"));
        Assert.Equal(0L, await CountAsync("CustomSeasonRequests"));
        Assert.Equal(0L, await CountAsync("CustomSonarrSeasonStatus"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM pragma_foreign_key_check"));

        // The old stores read their rows as before, and the new tables work.
        var factory = new Factory(_options);
        using (var requests = new DiscoveryRequestStore(factory))
        {
            Assert.Equal(DiscoveryRequest.StateRequested, (await requests.GetAsync(456, DiscoveryMediaType.Series, Ct))!.State);
            Assert.Equal(DiscoveryRequest.StateDownloading, (await requests.GetAsync(30, Ct))!.State);
        }

        using (var seasons = new SeasonStore(factory))
        {
            await seasons.RequestAsync(456, [4, 5], _userA, Ct);
            Assert.Equal(new[] { 4, 5 }, (await seasons.GetPendingAsync(Ct)).Select(r => r.SeasonNumber));
            await seasons.ReplaceSonarrStatusAsync([new SonarrSeasonStatus(456, 4, SonarrSeasonStatus.StatusWanted, false)], Ct);
            Assert.Single(await seasons.GetSonarrStatusAsync(456, Ct));
        }
    }

    [Fact]
    public async Task MigrationDown_DropsTheNewTablesOnly()
    {
        await using (var context = new CustomDbContext(_options))
        {
            await context.Database.MigrateAsync(Ct);
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken: Ct);
        }

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('CustomSeasonRequests', 'CustomSonarrSeasonStatus')"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'CustomDiscoveryRequests'"));
    }

    private static string Text(Guid id) => id.ToString().ToUpperInvariant();

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only SQL built from the test's own constants.")]
    private async Task ExecAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private Task<long> CountAsync(string table) => ScalarAsync($"SELECT COUNT(*) FROM \"{table}\"");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only SQL built from the test's own constants.")]
    private async Task<long> ScalarAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Factory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }
}
