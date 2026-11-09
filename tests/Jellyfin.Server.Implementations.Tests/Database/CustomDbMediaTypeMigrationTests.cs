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
/// The media type migration rebuilds the TMDB-keyed tables with the type in their keys. A database at the schema before
/// it, with rows the way the stores write them, must come out with every row kept and read back as a movie.
/// </summary>
public sealed class CustomDbMediaTypeMigrationTests : IDisposable
{
    // The last migration before the media type.
    private const string PreviousMigration = "20261109042319_AddDiscoveryRequests";

    private static readonly Guid _userA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid _userB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid _savedA = Guid.Parse("01010101-0202-0303-0404-050505050505");
    private static readonly Guid _savedB = Guid.Parse("0a0a0a0a-0b0b-0c0c-0d0d-0e0e0e0e0e0e");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CustomDbContext> _options;

    public CustomDbMediaTypeMigrationTests()
    {
        _connection.Open();

        // As on the server, where EF opens the connection with foreign keys on: a rebuild that dropped a parent table
        // with them on would cascade its children away.
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
    public async Task Migration_PreservesExistingRowsAsMovie()
    {
        await using (var context = new CustomDbContext(_options))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken: Ct);
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var day = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await ExecAsync(
            "INSERT INTO CustomDiscoveryLists (Id, Title, Source, FilmsJson, \"Group\", DateModified) VALUES "
            + "('best-of', 'Best of', 'app', '[{\"TmdbId\":30,\"ImdbId\":\"tt30\"},{\"TmdbId\":10,\"ImdbId\":null}]', 'genre:drama', '2026-11-08 10:00:00'), "
            + "('plain', 'Plain', 'app', '[{\"TmdbId\":20,\"ImdbId\":null}]', NULL, '2026-11-08 11:00:00')");
        await ExecAsync($"INSERT INTO CustomDiscoveryShown (TmdbId, FirstShown, LastShown) VALUES (30, '{yesterday}', '{day}'), (10, '{day}', '{day}')");
        await ExecAsync($"INSERT INTO CustomDiscoveryPickSalts (Day, Salt, DateCreated) VALUES ('{day}', 'salt-1', '2026-11-08 09:00:00')");
        await ExecAsync(
            $"INSERT INTO CustomDiscoveryPickSnapshots (UserId, Day, ListIdsJson) VALUES ('{Text(_userA)}', '{day}', '[\"best-of\",\"plain\"]'), "
            + $"('{Text(_userB)}', '{day}', '[\"plain\"]')");
        await ExecAsync(
            $"INSERT INTO CustomUserLists (Id, UserId, Name, IsDefault, DateCreated) VALUES ('{Text(_savedA)}', '{Text(_userA)}', 'Saved', 1, '2026-11-01 08:00:00'), "
            + $"('{Text(_savedB)}', '{Text(_userB)}', 'Saved', 1, '2026-11-01 08:00:00')");
        await ExecAsync(
            $"INSERT INTO CustomUserListItems (ListId, TmdbId, ImdbId, AddedAt) VALUES ('{Text(_savedA)}', 30, 'tt30', '2026-11-02 08:00:00'), "
            + $"('{Text(_savedA)}', 20, NULL, '2026-11-03 08:00:00'), ('{Text(_savedB)}', 30, 'tt30', '2026-11-04 08:00:00')");
        await ExecAsync(
            "INSERT INTO CustomDiscoveryRequests (TmdbId, State, UpdatedAt) VALUES (30, 'downloading', '2026-11-05 08:00:00'), "
            + "(20, 'done', '2026-11-06 08:00:00'), (10, 'failed', '2026-11-07 08:00:00')");
        await ExecAsync(
            $"INSERT INTO CustomDiscoveryRequestUsers (TmdbId, UserId) VALUES (30, '{Text(_userA)}'), (30, '{Text(_userB)}'), (20, '{Text(_userA)}')");
        await ExecAsync("INSERT INTO CustomDiscoverySettings (Key, Value) VALUES ('downloads-enabled', 'True')");
        await ExecAsync("INSERT INTO CustomMarkers (Id, DateApplied) VALUES ('saved-row-default', '2026-11-08 12:00:00')");

        await using (var context = new CustomDbContext(_options))
        {
            await context.Database.MigrateAsync(Ct);
        }

        // Every row kept, and the foreign keys still hold.
        Assert.Equal(2L, await CountAsync("CustomDiscoveryLists"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryShown"));
        Assert.Equal(1L, await CountAsync("CustomDiscoveryPickSalts"));
        Assert.Equal(2L, await CountAsync("CustomDiscoveryPickSnapshots"));
        Assert.Equal(2L, await CountAsync("CustomUserLists"));
        Assert.Equal(3L, await CountAsync("CustomUserListItems"));
        Assert.Equal(3L, await CountAsync("CustomDiscoveryRequests"));
        Assert.Equal(3L, await CountAsync("CustomDiscoveryRequestUsers"));
        Assert.Equal(1L, await CountAsync("CustomDiscoverySettings"));
        Assert.Equal(1L, await CountAsync("CustomMarkers"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM pragma_foreign_key_check"));

        // Each typed row is a movie.
        foreach (var (table, column) in new[]
        {
            ("CustomDiscoveryLists", "ItemType"),
            ("CustomDiscoveryShown", "MediaType"),
            ("CustomDiscoveryPickSnapshots", "MediaType"),
            ("CustomUserListItems", "MediaType"),
            ("CustomDiscoveryRequests", "MediaType"),
            ("CustomDiscoveryRequestUsers", "MediaType"),
        })
        {
            Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE {column} IS NOT 'Movie'"));
        }

        // The stores read the old rows as they did before.
        var factory = new Factory(_options);
        using (var store = new DiscoveryStore(factory))
        {
            var lists = await store.GetListsAsync(Ct);
            Assert.Equal(new[] { "best-of", "plain" }, lists.Select(l => l.Id));
            Assert.All(lists, l => Assert.Equal(DiscoveryMediaType.Movie, l.ItemType));
            Assert.Equal(
                new[] { new DiscoveryFilmRef(30, "tt30"), new DiscoveryFilmRef(10, null) },
                lists[0].Films);
            Assert.Equal("genre:drama", lists[0].Group);

            var shown = await store.GetShownAsync(Ct);
            Assert.Equal(new DiscoveryShown(30, today.AddDays(-1), today), shown[30]);
            Assert.Equal(new DiscoveryShown(10, today, today), shown[10]);
            Assert.Empty(await store.GetShownAsync(DiscoveryMediaType.Series, Ct));

            Assert.Equal("salt-1", await store.GetPickSaltAsync(today, Ct));
            Assert.Equal(new[] { "best-of", "plain" }, await store.GetPickSnapshotAsync(_userA, today, Ct));
            Assert.Equal(new[] { "plain" }, await store.GetPickSnapshotAsync(_userB, today, Ct));
            Assert.Null(await store.GetPickSnapshotAsync(_userA, today, DiscoveryMediaType.Series, Ct));

            // A first save still loses to the kept one.
            Assert.Equal(new[] { "best-of", "plain" }, await store.AddPickSnapshotAsync(_userA, today, "salt-1", ["plain"], Ct));
        }

        using (var userLists = new UserListStore(factory))
        {
            var saved = await userLists.GetDefaultListAsync(_userA, Ct);
            Assert.Equal(_savedA, saved.Id);
            Assert.Equal(new[] { 20, 30 }, saved.Films.Select(f => f.TmdbId));
            Assert.Equal(new string?[] { null, "tt30" }, saved.Films.Select(f => f.ImdbId));
            Assert.All(saved.Films, f => Assert.Equal(DiscoveryMediaType.Movie, f.Type));
            Assert.True(saved.Contains(30));
            Assert.Equal(new[] { 20, 30 }, (await userLists.GetListedTmdbIdsAsync(Ct)).Order());

            // The toggle still finds the old row: it goes rather than a second one being added.
            Assert.False(await userLists.ToggleAsync(_userB, _savedB, 30, "tt30", Ct));
            Assert.Empty((await userLists.GetDefaultListAsync(_userB, Ct)).Films);
        }

        using (var requests = new DiscoveryRequestStore(factory))
        {
            var downloading = (await requests.GetAsync(30, Ct))!;
            Assert.Equal(DiscoveryRequest.StateDownloading, downloading.State);
            Assert.Equal(DiscoveryMediaType.Movie, downloading.Type);
            Assert.Equal(new[] { _userA, _userB }.Order(), downloading.UserIds.Order());
            Assert.Equal(new[] { 10, 20, 30 }, (await requests.GetAllAsync(null, Ct)).Select(r => r.TmdbId).Order());
            Assert.Equal(new[] { 10, 30 }, (await requests.GetActiveTmdbIdsAsync(Ct)).Order());
            Assert.True(await requests.GetDownloadsEnabledAsync(Ct));

            // Joining the old request adds a requester to it, not a new request.
            var joined = await requests.RequestAsync(20, _userB, Ct);
            Assert.Equal(new[] { _userA, _userB }.Order(), joined.UserIds.Order());
            Assert.Equal(3, (await requests.GetAllAsync(null, Ct)).Count);
        }

        // Cascades still run on the rebuilt tables.
        Assert.Equal(4L, await CountAsync("CustomDiscoveryRequestUsers"));
        await ExecAsync("DELETE FROM CustomDiscoveryRequests WHERE TmdbId = 30");
        Assert.Equal(2L, await CountAsync("CustomDiscoveryRequestUsers"));
    }

    [Fact]
    public async Task MigrationDown_DropsShowLists_KeepsMovieLists()
    {
        await using (var context = new CustomDbContext(_options))
        {
            await context.Database.MigrateAsync(Ct);
        }

        using (var store = new DiscoveryStore(new Factory(_options)))
        {
            await store.SetListAsync(new DiscoveryList("films", "Films", "app", [new(30, null)]), Ct);
            await store.SetListAsync(new DiscoveryList("shows", "Shows", "app", [new(30, null)], ItemType: DiscoveryMediaType.Series), Ct);
        }

        await using (var context = new CustomDbContext(_options))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken: Ct);
        }

        // After a rollback, a show list left behind would read as a movie list.
        Assert.Equal(1L, await CountAsync("CustomDiscoveryLists"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM CustomDiscoveryLists WHERE Id = 'films'"));
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
