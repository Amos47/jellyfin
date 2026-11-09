using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

public sealed class DiscoveryRowPlannerTests : IDisposable
{
    private static readonly DateOnly _day = new(2026, 11, 7);

    private readonly ILibraryManager _previousLibraryManager = BaseItem.LibraryManager;
    private readonly HashSet<Guid> _withTrailer = [];
    private readonly Dictionary<(Guid User, DateOnly Day), IReadOnlyList<string>> _snapshots = [];

    private readonly Mock<IDiscoveryStore> _store = new();
    private readonly Mock<IDiscoveryFilmSync> _sync = new();
    private readonly User _user = new("tester", "Default", "Default");
    private readonly Dictionary<int, Movie> _films = [];
    private readonly Dictionary<int, BaseItem> _owned = [];
    private readonly DiscoveryRowPlanner _planner;

    public DiscoveryRowPlannerTests()
    {
        _sync.Setup(s => s.FindFilm(It.IsAny<int>())).Returns<int>(id => _films.GetValueOrDefault(id));
        _sync.Setup(s => s.FindOwned(_user, It.IsAny<int>(), It.IsAny<string?>())).Returns<User, int, string?>((_, id, _) => _owned.GetValueOrDefault(id));
        _planner = new DiscoveryRowPlanner(_store.Object, _sync.Object);

        // GetExtras goes through the static library manager: a film in _withTrailer has one local trailer.
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.OwnerIds.Any(_withTrailer.Contains) ? [new Video { Id = Guid.NewGuid() }] : []);
        BaseItem.LibraryManager = libraryManager.Object;

        // The saved picks, kept the way the store keeps them: the first save wins.
        _store
            .Setup(s => s.GetPickSnapshotAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, DateOnly day, CancellationToken _) => _snapshots.GetValueOrDefault((user, day)));
        _store
            .Setup(s => s.AddPickSnapshotAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, DateOnly day, string? _, IReadOnlyList<string> ids, CancellationToken _) => _snapshots.TryAdd((user, day), ids.ToArray()) ? ids : _snapshots[(user, day)]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => BaseItem.LibraryManager = _previousLibraryManager;

    private void SetLists(params DiscoveryList[] lists)
        => _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(lists);

    private static DiscoveryList List(string id, params int[] tmdbIds)
        => new(id, "Title of " + id, "tests", tmdbIds.Select(t => new DiscoveryFilmRef(t, null)).ToArray());

    private Movie Film(int tmdbId, bool visible = true, bool trailer = false)
    {
        var film = new Mock<Movie> { CallBase = true };
        film.Setup(f => f.IsVisibleStandalone(_user)).Returns(visible);
        film.Object.Id = Guid.NewGuid();
        if (trailer)
        {
            _withTrailer.Add(film.Object.Id);
        }

        _films[tmdbId] = film.Object;
        return film.Object;
    }

    [Fact]
    public async Task PickedLists_OnlyListsWithAVisibleFilm()
    {
        Film(1, visible: false);
        Film(2);
        Film(3, visible: false);
        _owned[3] = new Movie { Id = Guid.NewGuid() };
        SetLists(List("hidden", 1), List("visible", 1, 2), List("owned", 3), List("missing", 99), List("empty"));

        var picked = await _planner.GetPickedListsAsync(_user, 10, DiscoveryPickRules.AllVisible, _day, Ct);

        Assert.Equal(new[] { "owned", "visible" }, picked.Select(l => l.Id).Order(StringComparer.Ordinal));
        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, ["visible", "owned"], 10), picked.Select(l => l.Id));
    }

    [Fact]
    public async Task PickedLists_CappedAtMaxRows()
    {
        Film(1);
        SetLists(List("a", 1), List("b", 1), List("c", 1));

        var picked = await _planner.GetPickedListsAsync(_user, 2, DiscoveryPickRules.AllVisible, _day, Ct);

        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, ["a", "b", "c"], 2), picked.Select(l => l.Id));
    }

    [Fact]
    public async Task PickedLists_UseTheDaysStoredSalt()
    {
        Film(1);
        var lists = new[] { List("a", 1), List("b", 1), List("c", 1), List("d", 1), List("e", 1) };
        SetLists(lists);
        _store.Setup(s => s.GetPickSaltAsync(_day, It.IsAny<CancellationToken>())).ReturnsAsync("salt-x");

        var picked = await _planner.GetPickedListsAsync(_user, 3, DiscoveryPickRules.AllVisible, _day, Ct);

        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, lists, 3, "salt-x").Select(l => l.Id), picked.Select(l => l.Id));
        _store.Verify(s => s.GetPickSaltAsync(_day, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task PickedLists_OneListPerGroup_IneligibleListLeavesGroupOpen()
    {
        Film(1);
        Film(2, visible: false);
        SetLists(
            new DiscoveryList("hidden-horror", "Hidden", "tests", [new DiscoveryFilmRef(2, null)], "horror"),
            new DiscoveryList("horror-1", "H1", "tests", [new DiscoveryFilmRef(1, null)], "horror"),
            new DiscoveryList("horror-2", "H2", "tests", [new DiscoveryFilmRef(1, null)], "horror"),
            new DiscoveryList("drama-1", "D1", "tests", [new DiscoveryFilmRef(1, null)], "drama"));

        var picked = await _planner.GetPickedListsAsync(_user, 5, DiscoveryPickRules.AllVisible, _day, Ct);

        Assert.Equal(2, picked.Count);
        Assert.Equal(new[] { "drama", "horror" }, picked.Select(l => l.Group!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(picked, l => l.Id == "hidden-horror");
    }

    [Fact]
    public void RowItems_ListOrder_OwnedReplacesVirtual_HiddenLeftOut()
    {
        var first = Film(10);
        Film(11, visible: false);
        Film(12);
        var owned = new Movie { Id = Guid.NewGuid() };
        _owned[12] = owned;
        var last = Film(13);

        var items = _planner.GetRowItems(_user, List("a", 10, 11, 12, 99, 13));

        Assert.Equal(new[] { first.Id, owned.Id, last.Id }, items.Select(i => i.Item.Id));
        Assert.Equal(new[] { 10, 12, 13 }, items.Select(i => i.Film.TmdbId));
    }

    [Fact]
    public void Rows_RequireTrailer_HidesVirtualFilmsWithoutTrailers_KeepsOwned()
    {
        var withTrailer = Film(10, trailer: true);
        Film(11);
        Film(12, visible: false, trailer: true);
        Film(13);
        var owned = new Movie { Id = Guid.NewGuid() };
        _owned[13] = owned;
        var last = Film(14, trailer: true);

        var items = _planner.GetRowItems(_user, List("a", 10, 11, 12, 13, 99, 14), requireTrailer: true);

        Assert.Equal(new[] { withTrailer.Id, owned.Id, last.Id }, items.Select(i => i.Item.Id));
        Assert.Equal(new[] { 10, 13, 14 }, items.Select(i => i.Film.TmdbId));
    }

    [Fact]
    public void Rows_RequireTrailerOff_ShowsAllVisible()
    {
        var withTrailer = Film(10, trailer: true);
        var plain = Film(11);
        Film(12, visible: false, trailer: true);
        var owned = new Movie { Id = Guid.NewGuid() };
        _owned[13] = owned;
        var list = List("a", 10, 11, 12, 13);

        var items = _planner.GetRowItems(_user, list, requireTrailer: false);

        Assert.Equal(new[] { withTrailer.Id, plain.Id, owned.Id }, items.Select(i => i.Item.Id));
        Assert.Equal(items, _planner.GetRowItems(_user, list));
    }

    [Fact]
    public void CountReadyFilms_OwnedPlusVisibleWithTrailer()
    {
        Film(1, trailer: true);
        Film(2);
        Film(3, visible: false, trailer: true);
        Film(4);
        _owned[4] = new Movie { Id = Guid.NewGuid() };
        _owned[5] = new Movie { Id = Guid.NewGuid() };

        Assert.Equal(3, _planner.CountReadyFilms(_user, List("a", 1, 2, 3, 4, 5, 99)));
        Assert.Equal(0, _planner.CountReadyFilms(_user, List("b", 2, 3)));
    }

    [Fact]
    public async Task Picks_OnlyListsWithMinReadyFilms()
    {
        Film(1, trailer: true);
        Film(2, trailer: true);
        Film(3);
        Film(4, visible: false, trailer: true);
        _owned[5] = new Movie { Id = Guid.NewGuid() };
        var lists = new[]
        {
            List("two-trailers", 1, 2),
            List("trailer-and-owned", 1, 5),
            List("one-ready", 1, 3, 4),
            List("none-ready", 3, 4),
        };
        SetLists(lists);

        var picked = await _planner.GetPickedListsAsync(_user, 10, new DiscoveryPickRules(true, 2), _day, Ct);
        var lenient = await _planner.GetPickedListsAsync(_user, 10, new DiscoveryPickRules(true, 1), _day.AddDays(1), Ct);

        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, lists[..2], 10, null).Select(l => l.Id), picked.Select(l => l.Id));
        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day.AddDays(1), lists[..3], 10, null).Select(l => l.Id), lenient.Select(l => l.Id));
    }

    [Fact]
    public async Task Picks_GroupsAndSaltApply_AmongReadyLists()
    {
        Film(1, trailer: true);
        Film(2);
        var lists = new[]
        {
            new DiscoveryList("h-ready", "H", "tests", [new DiscoveryFilmRef(1, null)], "horror"),
            new DiscoveryList("h-cold", "H2", "tests", [new DiscoveryFilmRef(2, null)], "horror"),
            new DiscoveryList("d-ready", "D", "tests", [new DiscoveryFilmRef(1, null)], "drama"),
            new DiscoveryList("d-ready-2", "D2", "tests", [new DiscoveryFilmRef(1, null)], "drama"),
            List("x-ready", 1),
        };
        SetLists(lists);
        _store.Setup(s => s.GetPickSaltAsync(_day, It.IsAny<CancellationToken>())).ReturnsAsync("salt-y");

        var picked = await _planner.GetPickedListsAsync(_user, 10, new DiscoveryPickRules(true, 1), _day, Ct);

        var ready = lists.Where(l => l.Id != "h-cold");
        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, ready, 10, "salt-y").Select(l => l.Id), picked.Select(l => l.Id));
        Assert.Equal(3, picked.Count);
    }

    [Fact]
    public async Task Picks_NoReadyLists_ZeroRows()
    {
        Film(1);
        Film(2, visible: false, trailer: true);
        SetLists(List("a", 1), List("b", 1, 2));

        var picked = await _planner.GetPickedListsAsync(_user, 3, DiscoveryPickRules.Default, _day, Ct);

        Assert.Empty(picked);

        // Nothing is saved: the first non-empty pick of the day is the one kept.
        Assert.Empty(_snapshots);
    }

    [Fact]
    public async Task Picks_Saved_SmallerMaxRowsTakesAPrefix()
    {
        Film(1, trailer: true);
        var lists = new[] { List("a", 1), List("b", 1), List("c", 1), List("d", 1) };
        SetLists(lists);
        var rules = new DiscoveryPickRules(true, 1);

        var two = await _planner.GetPickedListsAsync(_user, 2, rules, _day, Ct);
        var all = await _planner.GetPickedListsAsync(_user, 10, rules, _day, Ct);

        var expected = DiscoveryPicker.Pick(_user.Id, _day, lists, 10, null).Select(l => l.Id).ToArray();
        Assert.Equal(expected, _snapshots[(_user.Id, _day)]);
        Assert.Equal(expected[..2], two.Select(l => l.Id));
        Assert.Equal(expected, all.Select(l => l.Id));
    }

    [Fact]
    public async Task Picks_RequireTrailerOff_NothingSaved()
    {
        Film(1);
        SetLists(List("a", 1), List("b", 1));

        var picked = await _planner.GetPickedListsAsync(_user, 3, DiscoveryPickRules.AllVisible, _day, Ct);

        Assert.Equal(2, picked.Count);
        Assert.Empty(_snapshots);
        _store.Verify(s => s.GetPickSnapshotAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
