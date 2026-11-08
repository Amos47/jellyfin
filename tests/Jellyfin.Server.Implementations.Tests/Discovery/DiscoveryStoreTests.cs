using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Custom.Database;
using MediaBrowser.Controller.Discovery;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

public sealed class DiscoveryStoreTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DiscoveryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CustomDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
            .Options;
        await using (var context = new CustomDbContext(options))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        _store = new DiscoveryStore(new Factory(options));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SetList_ThenGet_RoundTripsOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var list = new DiscoveryList("best-of", "Best of", "test", [new(30, "tt30"), new(10, null), new(20, "tt20")]);

        await _store.SetListAsync(list, ct);

        var read = await _store.GetListAsync("best-of", ct);
        Assert.NotNull(read);
        Assert.Equal("Best of", read.Title);
        Assert.Equal("test", read.Source);
        Assert.Equal(new[] { 30, 10, 20 }, read.Films.Select(f => f.TmdbId));
        Assert.Equal(new string?[] { "tt30", null, "tt20" }, read.Films.Select(f => f.ImdbId));
        Assert.Single(await _store.GetListsAsync(ct));
    }

    [Fact]
    public async Task SetList_SameId_Replaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetListAsync(new DiscoveryList("a", "One", "s", [new(1, null)]), ct);
        await _store.SetListAsync(new DiscoveryList("a", "Two", "s2", [new(2, null), new(3, null)]), ct);

        var lists = await _store.GetListsAsync(ct);
        var only = Assert.Single(lists);
        Assert.Equal("Two", only.Title);
        Assert.Equal("s2", only.Source);
        Assert.Equal(2, only.Films.Count);
    }

    [Fact]
    public async Task RemoveList_Missing_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await _store.RemoveListAsync("nope", ct));

        await _store.SetListAsync(new DiscoveryList("a", "One", "s", []), ct);
        Assert.True(await _store.RemoveListAsync("a", ct));
        Assert.Null(await _store.GetListAsync("a", ct));
    }

    [Fact]
    public async Task MarkShown_KeepsFirstShown_UpdatesLastShown()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.MarkShownAsync([5, 6], new DateOnly(2026, 11, 7), ct);
        await _store.MarkShownAsync([5, 7, 5], new DateOnly(2026, 11, 9), ct);

        var shown = await _store.GetShownAsync(ct);
        Assert.Equal(3, shown.Count);
        Assert.Equal(new DiscoveryShown(5, new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 9)), shown[5]);
        Assert.Equal(new DiscoveryShown(6, new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 7)), shown[6]);
        Assert.Equal(new DiscoveryShown(7, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9)), shown[7]);
    }

    [Fact]
    public async Task SetList_ConcurrentSameNewId_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await ConcurrentDb.CreateAsync(ct);

        await Task.WhenAll(Enumerable.Range(0, ConcurrentDb.Callers).Select(i =>
            db.Store.SetListAsync(new DiscoveryList("same", "T" + i, "s", [new(i, null)]), ct)));

        Assert.Single(await db.Store.GetListsAsync(ct));
    }

    [Fact]
    public async Task MarkShown_ConcurrentSameNewId_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await ConcurrentDb.CreateAsync(ct);

        await Task.WhenAll(Enumerable.Range(0, ConcurrentDb.Callers).Select(_ =>
            db.Store.MarkShownAsync([42, 43], new DateOnly(2026, 11, 7), ct)));

        Assert.Equal(2, (await db.Store.GetShownAsync(ct)).Count);
    }

    private sealed class Factory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// A file backed db with a connection per context, whose factory holds the first callers until they have all
    /// arrived (or a short timeout passes, which is what an unlocked store never needs), so unserialized check-then-insert
    /// code interleaves for real.
    /// </summary>
    private sealed class ConcurrentDb : IDbContextFactory<CustomDbContext>, IDisposable
    {
        public const int Callers = 4;

        private readonly string _path = Path.Combine(Path.GetTempPath(), "discovery-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly DbContextOptions<CustomDbContext> _options;
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        private ConcurrentDb()
        {
            _options = new DbContextOptionsBuilder<CustomDbContext>()
                .UseSqlite("Data Source=" + _path, o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
                .Options;
            Store = new DiscoveryStore(this);
        }

        public DiscoveryStore Store { get; }

        public static async Task<ConcurrentDb> CreateAsync(CancellationToken ct)
        {
            var db = new ConcurrentDb();
            await using var context = new CustomDbContext(db._options);
            await context.Database.MigrateAsync(ct);
            return db;
        }

        public CustomDbContext CreateDbContext() => new(_options);

        public async Task<CustomDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) <= Callers)
            {
                if (_arrived == Callers)
                {
                    _allArrived.TrySetResult();
                }

                await Task.WhenAny(_allArrived.Task, Task.Delay(150, cancellationToken));
            }

            return CreateDbContext();
        }

        public void Dispose()
        {
            Store.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(_path);
        }
    }
}
