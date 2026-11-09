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
using MediaBrowser.Controller.Entities.TV;
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
    private readonly FakeTime _time = new();
    private readonly DiscoveryRowPlanner _planner;

    public DiscoveryRowPlannerTests()
    {
        _sync.Setup(s => s.FindFilm(It.IsAny<int>())).Returns<int>(id => _films.GetValueOrDefault(id));
        _sync.Setup(s => s.FindOwned(_user, It.IsAny<int>(), It.IsAny<string?>())).Returns<User, int, string?>((_, id, _) => _owned.GetValueOrDefault(id));
        _planner = new DiscoveryRowPlanner(_store.Object, _sync.Object, _time);

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
        _store
            .Setup(s => s.RemovePickSnapshotAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, DateOnly day, IReadOnlyList<string> ids, CancellationToken _)
                => _snapshots.TryGetValue((user, day), out var saved) && saved.SequenceEqual(ids) && _snapshots.Remove((user, day)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => BaseItem.LibraryManager = _previousLibraryManager;

    private void SetLists(params DiscoveryList[] lists)
        => _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(lists);

    private int FindFilmCalls() => _sync.Invocations.Count(i => i.Method.Name == nameof(IDiscoveryFilmSync.FindFilm));

    private void AddTrailer(int tmdbId) => _withTrailer.Add(_films[tmdbId].Id);

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

        // Nothing is saved: only a pick that fills maxRows is kept.
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

    [Fact]
    public async Task Picks_PartialPickNotSaved_GrowsAsListsBecomeReady_ThenFreezesWhenFull()
    {
        Film(1, trailer: true);
        Film(2);
        Film(3);
        Film(4);
        var lists = new[] { List("a", 1), List("b", 2), List("c", 3), List("d", 4) };
        SetLists(lists);
        var rules = new DiscoveryPickRules(true, 1);

        // One ready list where three are asked for: shown, not saved.
        var one = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);
        Assert.Equal(new[] { "a" }, one.Select(l => l.Id));
        Assert.Empty(_snapshots);

        // A second list becomes ready (a trailer is registered): the pick grows, still unsaved.
        AddTrailer(2);
        _planner.InvalidateReadiness();
        var two = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);
        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, lists[..2], 10, null).Select(l => l.Id), two.Select(l => l.Id));
        Assert.Empty(_snapshots);

        // A third: the pick is full, so it is saved.
        AddTrailer(3);
        _planner.InvalidateReadiness();
        var full = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);
        var expected = DiscoveryPicker.Pick(_user.Id, _day, lists[..3], 10, null).Select(l => l.Id).ToArray();
        Assert.Equal(expected, full.Select(l => l.Id));
        Assert.Equal(expected, _snapshots[(_user.Id, _day)]);

        // Frozen: a fourth list becoming ready no longer changes the day's picks.
        AddTrailer(4);
        _planner.InvalidateReadiness();
        var later = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);
        Assert.Equal(expected, later.Select(l => l.Id));
    }

    [Fact]
    public async Task Picks_FewerGroupsThanMaxRows_FreezesOnceAllCanBePicked()
    {
        Film(1, trailer: true);
        Film(2);
        var lists = new[] { List("a", 1), List("b", 2) };
        SetLists(lists);
        var rules = new DiscoveryPickRules(true, 1);

        // Two lists can never fill three rows: one ready is not enough to save.
        Assert.Single(await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct));
        Assert.Empty(_snapshots);

        // Both ready: every list that can be picked is, so the pick is saved.
        AddTrailer(2);
        _planner.InvalidateReadiness();
        var both = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);
        var expected = DiscoveryPicker.Pick(_user.Id, _day, lists, 10, null).Select(l => l.Id).ToArray();
        Assert.Equal(expected, both.Select(l => l.Id));
        Assert.Equal(expected, _snapshots[(_user.Id, _day)]);
    }

    [Fact]
    public async Task Picks_Capacity_CountsEachGroupOnce_UngroupedListsEach()
    {
        Film(1, trailer: true);
        Film(2);
        var lists = new[]
        {
            new DiscoveryList("g-1", "G1", "tests", [new DiscoveryFilmRef(1, null)], "g"),
            new DiscoveryList("g-2", "G2", "tests", [new DiscoveryFilmRef(1, null)], "g"),
            new DiscoveryList("blank", "B", "tests", [new DiscoveryFilmRef(2, null)], " "),
        };
        SetLists(lists);
        var rules = new DiscoveryPickRules(true, 1);

        // Capacity 2 (group "g" plus the blank-group list): one group ready is not enough.
        Assert.Single(await _planner.GetPickedListsAsync(_user, 5, rules, _day, Ct));
        Assert.Empty(_snapshots);

        AddTrailer(2);
        _planner.InvalidateReadiness();
        Assert.Equal(2, (await _planner.GetPickedListsAsync(_user, 5, rules, _day, Ct)).Count);
        Assert.Equal(2, _snapshots[(_user.Id, _day)].Count);
    }

    [Fact]
    public async Task Picks_NoLists_NothingSaved()
    {
        SetLists();

        Assert.Empty(await _planner.GetPickedListsAsync(_user, 3, new DiscoveryPickRules(true, 1), _day, Ct));
        Assert.Empty(_snapshots);
    }

    [Fact]
    public async Task Picks_InvalidatedWhileCounting_ResultNotReused()
    {
        Film(1, trailer: true);
        Film(2);
        SetLists(List("b", 2), List("a", 1));
        var rules = new DiscoveryPickRules(true, 1);

        // A trailer lands while readiness is being counted (after "b" was counted): the count is stale and must not be served from the cache.
        var invalidated = false;
        _sync.Setup(s => s.FindFilm(It.IsAny<int>())).Returns<int>(id =>
        {
            if (id == 1 && !invalidated)
            {
                invalidated = true;
                AddTrailer(2);
                _planner.InvalidateReadiness();
            }

            return _films.GetValueOrDefault(id);
        });

        Assert.Single(await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct));
        Assert.Equal(2, (await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct)).Count);
    }

    [Fact]
    public async Task Picks_ReadinessCached_NoRecountWithinLifetime_InvalidatedByReshuffle()
    {
        Film(1, trailer: true);
        Film(2);
        var lists = new[] { List("a", 1), List("b", 2) };
        SetLists(lists);
        var rules = new DiscoveryPickRules(true, 1);

        Assert.Equal(new[] { "a" }, (await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct)).Select(l => l.Id));
        var counted = FindFilmCalls();

        // Within the lifetime the readiness is not counted again, so a new trailer is not seen yet.
        AddTrailer(2);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(new[] { "a" }, (await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct)).Select(l => l.Id));
        Assert.Equal(counted, FindFilmCalls());

        // A reshuffle invalidates it: the next read counts again.
        _planner.InvalidateReadiness();
        var after = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);
        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, lists, 10, null).Select(l => l.Id), after.Select(l => l.Id));
        Assert.True(FindFilmCalls() > counted);
    }

    [Fact]
    public async Task Picks_ReadinessCache_ExpiresAfterSixtySeconds_AndIsPerSalt()
    {
        Film(1, trailer: true);
        Film(2);
        Film(3);

        // Lists "c" and "d" never become ready, so the pick never fills three rows and is never saved.
        var lists = new[] { List("a", 1), List("b", 2), List("c", 3), List("d", 3) };
        SetLists(lists);
        var rules = new DiscoveryPickRules(true, 1);
        Assert.Single(await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct));
        AddTrailer(2);

        // A new salt (a re-pick) is a different key.
        _store.Setup(s => s.GetPickSaltAsync(_day, It.IsAny<CancellationToken>())).ReturnsAsync("salt-z");
        Assert.Equal(2, (await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct)).Count);

        // And an entry lapses after its lifetime.
        _store.Setup(s => s.GetPickSaltAsync(_day, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        Assert.Single(await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(2, (await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct)).Count);
    }

    [Fact]
    public async Task Picks_SnapshotWithAllListsDeleted_Recomputes()
    {
        Film(1, trailer: true);
        var lists = new[] { List("c", 1), List("d", 1) };
        SetLists(lists);
        _snapshots[(_user.Id, _day)] = ["gone-a", "gone-b"];

        var picked = await _planner.GetPickedListsAsync(_user, 2, new DiscoveryPickRules(true, 1), _day, Ct);

        var expected = DiscoveryPicker.Pick(_user.Id, _day, lists, 10, null).Select(l => l.Id).ToArray();
        Assert.Equal(expected, picked.Select(l => l.Id));

        // The dead snapshot is replaced by the new full pick.
        Assert.Equal(expected, _snapshots[(_user.Id, _day)]);
    }

    [Fact]
    public async Task Picks_SnapshotWithSomeListsDeleted_KeepsTheRest()
    {
        Film(1, trailer: true);
        SetLists(List("c", 1), List("d", 1));
        _snapshots[(_user.Id, _day)] = ["gone", "d"];

        var picked = await _planner.GetPickedListsAsync(_user, 2, new DiscoveryPickRules(true, 1), _day, Ct);

        Assert.Equal(new[] { "d" }, picked.Select(l => l.Id));
        Assert.Equal(new[] { "gone", "d" }, _snapshots[(_user.Id, _day)]);
    }

    [Fact]
    public void IsPlayable_OwnedOrVisibleWithTrailer()
    {
        Film(1, trailer: true);
        Film(2);
        Film(3, visible: false, trailer: true);
        _owned[4] = new Movie { Id = Guid.NewGuid() };

        Assert.True(_planner.IsPlayable(_user, new DiscoveryFilmRef(1, null)));
        Assert.False(_planner.IsPlayable(_user, new DiscoveryFilmRef(2, null)));
        Assert.False(_planner.IsPlayable(_user, new DiscoveryFilmRef(3, null)));
        Assert.True(_planner.IsPlayable(_user, new DiscoveryFilmRef(4, null)));
        Assert.False(_planner.IsPlayable(_user, new DiscoveryFilmRef(99, null)));
    }

    [Fact]
    public async Task PickedLists_Series_OnlySeriesLists_SnapshotKeyedBySeries()
    {
        // A film and a show with one TMDB id; each list resolves only its own type.
        Film(1, trailer: true);
        var show = new Mock<Series> { CallBase = true };
        show.Setup(f => f.IsVisibleStandalone(_user)).Returns(true);
        show.Object.Id = Guid.NewGuid();
        _withTrailer.Add(show.Object.Id);
        _sync.Setup(s => s.FindSeries(1)).Returns(show.Object);

        // A show's extras look up its library options (series grouping) first.
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.OwnerIds.Any(_withTrailer.Contains) ? [new Video { Id = Guid.NewGuid() }] : []);
        libraryManager.Setup(m => m.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new MediaBrowser.Model.Configuration.LibraryOptions { EnableAutomaticSeriesGrouping = false });
        BaseItem.LibraryManager = libraryManager.Object;
        var films = List("films", 1);
        var shows = new DiscoveryList("shows", "Shows", "tests", [new DiscoveryFilmRef(1, null, DiscoveryMediaType.Series)], null, DiscoveryMediaType.Series);
        SetLists(films, shows);
        var seriesSnapshots = new List<IReadOnlyList<string>>();
        _store
            .Setup(s => s.AddPickSnapshotAsync(_user.Id, _day, DiscoveryMediaType.Series, It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DateOnly, DiscoveryMediaType, string?, IReadOnlyList<string>, CancellationToken>((_, _, _, _, ids, _) => seriesSnapshots.Add(ids))
            .ReturnsAsync((Guid _, DateOnly _, DiscoveryMediaType _, string? _, IReadOnlyList<string> ids, CancellationToken _) => ids);
        var rules = new DiscoveryPickRules(true, 1);

        var pickedShows = await _planner.GetPickedListsAsync(_user, 3, rules, _day, DiscoveryMediaType.Series, Ct);
        var pickedFilms = await _planner.GetPickedListsAsync(_user, 3, rules, _day, Ct);

        Assert.Equal(new[] { "shows" }, pickedShows.Select(l => l.Id));
        Assert.Equal(new[] { "films" }, pickedFilms.Select(l => l.Id));
        Assert.Equal(new[] { "shows" }, Assert.Single(seriesSnapshots));
        Assert.Equal(new[] { "films" }, _snapshots[(_user.Id, _day)]);
        Assert.Equal(show.Object.Id, Assert.Single(_planner.GetRowItems(_user, shows, true)).Item.Id);
        Assert.Equal(_films[1].Id, Assert.Single(_planner.GetRowItems(_user, films, true)).Item.Id);
        _store.Verify(s => s.GetPickSnapshotAsync(_user.Id, _day, DiscoveryMediaType.Series, It.IsAny<CancellationToken>()));
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
