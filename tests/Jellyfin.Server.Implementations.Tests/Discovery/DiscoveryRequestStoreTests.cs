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

public sealed class DiscoveryRequestStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "discovery-requests-" + Guid.NewGuid().ToString("N") + ".db");
    private DbContextOptions<CustomDbContext> _options = null!;
    private DiscoveryRequestStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<CustomDbContext>()
            .UseSqlite("Data Source=" + _path, o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
            .Options;
        await using (var context = new CustomDbContext(_options))
        {
            await context.Database.MigrateAsync(Ct);
        }

        _store = new DiscoveryRequestStore(new Factory(_options));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Request_AddsEachRequesterOnce_KeepsAnActiveState()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var created = await _store.RequestAsync(11, first, Ct);
        Assert.Equal(DiscoveryRequest.StateRequested, created.State);
        Assert.Equal(new[] { first }, created.UserIds);

        Assert.NotNull(await _store.SetStateAsync(11, DiscoveryRequest.StateDownloading, Ct));
        await _store.RequestAsync(11, first, Ct);
        var joined = await _store.RequestAsync(11, second, Ct);

        // Still downloading: a second requester joins, the download isn't restarted.
        Assert.Equal(DiscoveryRequest.StateDownloading, joined.State);
        Assert.Equal(new[] { first, second }.OrderBy(g => g), joined.UserIds.OrderBy(g => g));
        Assert.Single(await _store.GetAllAsync(DiscoveryRequest.StateDownloading, Ct));
        Assert.Empty(await _store.GetAllAsync(DiscoveryRequest.StateRequested, Ct));
    }

    [Theory]
    [InlineData(DiscoveryRequest.StateFailed)]
    [InlineData(DiscoveryRequest.StateDone)]
    public async Task Request_ReopensAFailedOrDoneRequest(string state)
    {
        await _store.RequestAsync(12, Guid.NewGuid(), Ct);
        await _store.SetStateAsync(12, state, Ct);

        var reopened = await _store.RequestAsync(12, Guid.NewGuid(), Ct);

        Assert.Equal(DiscoveryRequest.StateRequested, reopened.State);
        Assert.Equal(2, reopened.UserIds.Count);
    }

    [Fact]
    public async Task ActiveIds_LeaveOutDone()
    {
        await _store.RequestAsync(21, Guid.NewGuid(), Ct);
        await _store.RequestAsync(22, Guid.NewGuid(), Ct);
        await _store.RequestAsync(23, Guid.NewGuid(), Ct);
        await _store.SetStateAsync(22, DiscoveryRequest.StateFailed, Ct);
        await _store.SetStateAsync(23, DiscoveryRequest.StateDone, Ct);

        Assert.Equal(new[] { 21, 22 }, (await _store.GetActiveTmdbIdsAsync(Ct)).Order());
        Assert.Null(await _store.SetStateAsync(99, DiscoveryRequest.StateFailed, Ct));
        Assert.Null(await _store.GetAsync(99, Ct));
    }

    [Theory]
    [InlineData(DiscoveryRequest.StateDownloading)]
    [InlineData(DiscoveryRequest.StateFailed)]
    [InlineData(DiscoveryRequest.StateRequested)]
    public async Task SetState_NeverLeavesDone(string state)
    {
        await _store.RequestAsync(41, Guid.NewGuid(), Ct);
        await _store.SetStateAsync(41, DiscoveryRequest.StateDone, Ct);

        // A report that raced the done marking: the request stays done.
        Assert.Equal(DiscoveryRequest.StateDone, (await _store.SetStateAsync(41, state, Ct))!.State);
        Assert.Equal(DiscoveryRequest.StateDone, (await _store.GetAsync(41, Ct))!.State);
    }

    [Fact]
    public async Task RemoveUser_DropsOnlyTheirRequesterRows()
    {
        var gone = Guid.NewGuid();
        var stays = Guid.NewGuid();
        await _store.RequestAsync(31, gone, Ct);
        await _store.RequestAsync(31, stays, Ct);

        await _store.RemoveUserAsync(gone, Ct);

        Assert.Equal(new[] { stays }, (await _store.GetAsync(31, Ct))!.UserIds);
    }

    [Fact]
    public async Task DownloadsEnabled_OffByDefault_Persists()
    {
        Assert.False(await _store.GetDownloadsEnabledAsync(Ct));

        await _store.SetDownloadsEnabledAsync(true, Ct);
        Assert.True(await _store.GetDownloadsEnabledAsync(Ct));

        using (var reopened = new DiscoveryRequestStore(new Factory(_options)))
        {
            Assert.True(await reopened.GetDownloadsEnabledAsync(Ct));
        }

        await _store.SetDownloadsEnabledAsync(false, Ct);
        Assert.False(await _store.GetDownloadsEnabledAsync(Ct));
    }

    private sealed class Factory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }
}
