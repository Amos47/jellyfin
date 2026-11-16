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

public sealed class SeasonStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "season-requests-" + Guid.NewGuid().ToString("N") + ".db");
    private DbContextOptions<CustomDbContext> _options = null!;
    private SeasonStore _store = null!;

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

        _store = new SeasonStore(new Factory(_options));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Request_IsIdempotent_AndRearmsFailed()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await _store.RequestAsync(100, [1, 2, 2], first, Ct);
        var created = await _store.GetAsync(100, Ct);
        Assert.Equal(new[] { 1, 2 }, created.Select(r => r.SeasonNumber));
        Assert.All(created, r => Assert.Equal(SeasonRequest.StatePending, r.State));

        // Pressing Get twice, or Get series over a pending season: no second row, the pending one is left alone.
        await _store.RequestAsync(100, [1, 2, 3], second, Ct);
        var again = await _store.GetAsync(100, Ct);
        Assert.Equal(new[] { 1, 2, 3 }, again.Select(r => r.SeasonNumber));
        Assert.Equal(created.Single(r => r.SeasonNumber == 1).UpdatedAt, again.Single(r => r.SeasonNumber == 1).UpdatedAt);
        Assert.Equal(first, await UserOfAsync(100, 1));
        Assert.Equal(second, await UserOfAsync(100, 3));

        // A failed season and a handled one go back to pending, the reason cleared; the first requester is kept.
        await _store.SetStateAsync(100, 1, SeasonRequest.StateFailed, "No root folder", Ct);
        await _store.SetStateAsync(100, 2, SeasonRequest.StateHandled, null, Ct);
        var failed = (await _store.GetAsync(100, Ct)).Single(r => r.SeasonNumber == 1);
        Assert.Equal(SeasonRequest.StateFailed, failed.State);
        Assert.Equal("No root folder", failed.Reason);

        await _store.RequestAsync(100, [1, 2], second, Ct);
        var rearmed = await _store.GetAsync(100, Ct);
        Assert.All(rearmed, r => Assert.Equal(SeasonRequest.StatePending, r.State));
        Assert.All(rearmed, r => Assert.Null(r.Reason));
        Assert.Equal(first, await UserOfAsync(100, 1));

        // Another series is another set of rows; an unknown row is left alone.
        await _store.SetStateAsync(999, 1, SeasonRequest.StateHandled, null, Ct);
        Assert.Empty(await _store.GetAsync(999, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetStateAsync(100, 1, "bogus", null, Ct));
    }

    [Fact]
    public async Task GetPending_GroupsBySeries()
    {
        var user = Guid.NewGuid();
        await _store.RequestAsync(200, [3, 1], user, Ct);
        await _store.RequestAsync(100, [2], user, Ct);
        await _store.RequestAsync(300, [1], user, Ct);
        await _store.SetStateAsync(200, 3, SeasonRequest.StateHandled, null, Ct);
        await _store.SetStateAsync(300, 1, SeasonRequest.StateFailed, "boom", Ct);

        var pending = await _store.GetPendingAsync(Ct);

        // Only pending rows, each series' seasons together and in order.
        Assert.Equal(
            new[] { (100, 2), (200, 1) },
            pending.Select(r => (r.SeriesTmdbId, r.SeasonNumber)));
        Assert.All(pending, r => Assert.Equal(SeasonRequest.StatePending, r.State));
    }

    [Fact]
    public async Task ReplaceSonarrStatus_ReplacesAll()
    {
        await _store.ReplaceSonarrStatusAsync(
            [
                new SonarrSeasonStatus(100, 1, SonarrSeasonStatus.StatusWanted, false),
                new SonarrSeasonStatus(100, 2, SonarrSeasonStatus.StatusDownloading, false),
                new SonarrSeasonStatus(200, 1, SonarrSeasonStatus.StatusIdle, true),
            ],
            Ct);
        Assert.Equal(2, (await _store.GetSonarrStatusAsync(100, Ct)).Count);

        await _store.ReplaceSonarrStatusAsync(
            [
                new SonarrSeasonStatus(100, 2, SonarrSeasonStatus.StatusIdle, true),
                new SonarrSeasonStatus(300, 4, SonarrSeasonStatus.StatusUnmonitored, false),
            ],
            Ct);

        // Rows the app no longer reports are gone, the rest are as last reported.
        Assert.Equal(new[] { new SonarrSeasonStatus(100, 2, SonarrSeasonStatus.StatusIdle, true) }, await _store.GetSonarrStatusAsync(100, Ct));
        Assert.Empty(await _store.GetSonarrStatusAsync(200, Ct));
        Assert.Equal(new[] { new SonarrSeasonStatus(300, 4, SonarrSeasonStatus.StatusUnmonitored, false) }, await _store.GetSonarrStatusAsync(300, Ct));

        await _store.ReplaceSonarrStatusAsync([], Ct);
        Assert.Empty(await _store.GetSonarrStatusAsync(100, Ct));
        Assert.Empty(await _store.GetSonarrStatusAsync(300, Ct));

        await Assert.ThrowsAsync<ArgumentException>(() => _store.ReplaceSonarrStatusAsync([new SonarrSeasonStatus(1, 1, "bogus", false)], Ct));
    }

    private async Task<Guid> UserOfAsync(int seriesTmdbId, int season)
    {
        await using var context = new CustomDbContext(_options);
        return await context.SeasonRequests
            .Where(r => r.SeriesTmdbId == seriesTmdbId && r.SeasonNumber == season)
            .Select(r => r.UserId)
            .SingleAsync(Ct);
    }

    private sealed class Factory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }
}
