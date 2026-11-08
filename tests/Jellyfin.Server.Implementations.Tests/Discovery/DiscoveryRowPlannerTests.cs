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
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

public class DiscoveryRowPlannerTests
{
    private static readonly DateOnly _day = new(2026, 11, 7);

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
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SetLists(params DiscoveryList[] lists)
        => _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(lists);

    private static DiscoveryList List(string id, params int[] tmdbIds)
        => new(id, "Title of " + id, "tests", tmdbIds.Select(t => new DiscoveryFilmRef(t, null)).ToArray());

    private Movie Film(int tmdbId, bool visible = true)
    {
        var film = new Mock<Movie> { CallBase = true };
        film.Setup(f => f.IsVisibleStandalone(_user)).Returns(visible);
        film.Object.Id = Guid.NewGuid();
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

        var picked = await _planner.GetPickedListsAsync(_user, 10, _day, Ct);

        Assert.Equal(new[] { "owned", "visible" }, picked.Select(l => l.Id).Order(StringComparer.Ordinal));
        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, ["visible", "owned"], 10), picked.Select(l => l.Id));
    }

    [Fact]
    public async Task PickedLists_CappedAtMaxRows()
    {
        Film(1);
        SetLists(List("a", 1), List("b", 1), List("c", 1));

        var picked = await _planner.GetPickedListsAsync(_user, 2, _day, Ct);

        Assert.Equal(DiscoveryPicker.Pick(_user.Id, _day, ["a", "b", "c"], 2), picked.Select(l => l.Id));
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
}
