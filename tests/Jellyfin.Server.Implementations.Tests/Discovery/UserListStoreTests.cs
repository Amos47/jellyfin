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

public sealed class UserListStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "user-lists-" + Guid.NewGuid().ToString("N") + ".db");
    private DbContextOptions<CustomDbContext> _options = null!;
    private UserListStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // A file, a connection per context: concurrent calls really run side by side.
        _options = new DbContextOptionsBuilder<CustomDbContext>()
            .UseSqlite("Data Source=" + _path, o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
            .Options;
        await using (var context = new CustomDbContext(_options))
        {
            await context.Database.MigrateAsync(Ct);
        }

        _store = new UserListStore(new Factory(_options));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Toggle_FlipsAndAnswersTheNewState()
    {
        var user = Guid.NewGuid();
        var saved = await _store.GetDefaultListAsync(user, Ct);

        Assert.True(await _store.ToggleAsync(user, saved.Id, 7, "tt7", Ct));
        Assert.True((await _store.GetDefaultListAsync(user, Ct)).Contains(7));
        Assert.False(await _store.ToggleAsync(user, saved.Id, 7, null, Ct));
        Assert.False((await _store.GetDefaultListAsync(user, Ct)).Contains(7));

        // Another user's list: no state.
        Assert.Null(await _store.ToggleAsync(Guid.NewGuid(), saved.Id, 7, null, Ct));
    }

    [Fact]
    public async Task Toggle_Concurrent_IsAtomic()
    {
        var user = Guid.NewGuid();
        var saved = await _store.GetDefaultListAsync(user, Ct);

        // Eight toggles at once: each sees the one before it, so half turn it on and it ends off.
        var states = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _store.ToggleAsync(user, saved.Id, 9, null, Ct)));

        Assert.Equal(4, states.Count(s => s == true));
        Assert.Equal(4, states.Count(s => s == false));
        Assert.False((await _store.GetDefaultListAsync(user, Ct)).Contains(9));
    }

    [Fact]
    public async Task RemoveUser_DropsTheirListsAndFilms()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await _store.AddFilmAsync(gone, (await _store.GetDefaultListAsync(gone, Ct)).Id, 11, null, Ct);
        await _store.AddFilmAsync(kept, (await _store.GetDefaultListAsync(kept, Ct)).Id, 12, null, Ct);

        await _store.RemoveUserAsync(gone, Ct);

        Assert.Equal(new[] { 12 }, (await _store.GetListedTmdbIdsAsync(Ct)).Order());
        await using var context = new CustomDbContext(_options);
        Assert.False(await context.UserLists.AnyAsync(l => l.UserId.Equals(gone), Ct));
    }

    [Fact]
    public async Task SameTmdbId_MovieAndSeries_DoNotCollide()
    {
        var user = Guid.NewGuid();
        var saved = await _store.GetDefaultListAsync(user, Ct);

        // Saved holds both types; a film and a show with one TMDB id are two entries.
        Assert.True(await _store.AddFilmAsync(user, saved.Id, 42, "tt42", Ct));
        Assert.True(await _store.AddFilmAsync(user, saved.Id, 42, DiscoveryMediaType.Series, null, Ct));
        var both = (await _store.GetListAsync(user, saved.Id, Ct))!;
        Assert.Equal(
            new[] { DiscoveryMediaType.Movie, DiscoveryMediaType.Series },
            both.Films.Select(f => f.Type).Order());
        Assert.True(both.Contains(42));
        Assert.True(both.Contains(42, DiscoveryMediaType.Series));
        Assert.Equal(
            new[] { DiscoveryMediaType.Movie, DiscoveryMediaType.Series },
            both.ToDiscoveryList().Films.Select(f => f.Type).Order());

        // Toggling the show leaves the film.
        Assert.False(await _store.ToggleAsync(user, saved.Id, 42, DiscoveryMediaType.Series, null, Ct));
        var filmOnly = (await _store.GetListAsync(user, saved.Id, Ct))!;
        Assert.Equal(new UserListFilm(42, "tt42", filmOnly.Films[0].AddedAt), Assert.Single(filmOnly.Films));
        Assert.False(filmOnly.Contains(42, DiscoveryMediaType.Series));
        Assert.True(await _store.ToggleAsync(user, saved.Id, 42, DiscoveryMediaType.Series, null, Ct));

        // Listed ids are per type; without a type they are the films'.
        Assert.True(await _store.AddFilmAsync(user, saved.Id, 43, DiscoveryMediaType.Series, null, Ct));
        Assert.Equal(new[] { 42 }, (await _store.GetListedTmdbIdsAsync(Ct)).Order());
        Assert.Equal(new[] { 42, 43 }, (await _store.GetListedTmdbIdsAsync(DiscoveryMediaType.Series, Ct)).Order());

        // Removing the film leaves the show.
        Assert.True(await _store.RemoveFilmAsync(user, saved.Id, 42, Ct));
        Assert.Empty(await _store.GetListedTmdbIdsAsync(Ct));
        Assert.Equal(new[] { 42, 43 }, (await _store.GetListedTmdbIdsAsync(DiscoveryMediaType.Series, Ct)).Order());
        Assert.True(await _store.RemoveFilmAsync(user, saved.Id, 43, DiscoveryMediaType.Series, Ct));
        Assert.Equal(new[] { 42 }, (await _store.GetListedTmdbIdsAsync(DiscoveryMediaType.Series, Ct)).Order());
    }

    private sealed class Factory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }
}
