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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

/// <summary>
/// Fork: discovery. The Watch trailer queue still plays the film's own trailers when the user's picks fail to load.
/// </summary>
public sealed class WatchTrailerActionTests : IDisposable
{
    private readonly ILibraryManager _previousLibraryManager = BaseItem.LibraryManager;
    private readonly Mock<IDiscoveryUserPicks> _picks = new();
    private readonly Mock<IDiscoveryRowPlanner> _planner = new();
    private readonly User _user = new("tester", "Default", "Default");

    public WatchTrailerActionTests()
    {
        // GetExtras goes through the static library manager.
        var trailer = new Video { Id = Guid.NewGuid() };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([trailer]);
        BaseItem.LibraryManager = libraryManager.Object;
        Trailer = trailer;
    }

    private Video Trailer { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => BaseItem.LibraryManager = _previousLibraryManager;

    [Fact]
    public async Task GetQueue_PicksFail_FallsBackToOwnTrailers()
    {
        _picks.Setup(p => p.GetPickedListsAsync(_user, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broken layout"));
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, Mock.Of<IDiscoveryFilmSync>(), Mock.Of<IDiscoveryStore>(), Mock.Of<IUserListStore>(), NullLogger<WatchTrailerAction>.Instance);

        var queue = await action.GetQueueAsync(_user, new Movie { Id = Guid.NewGuid() }, Ct);

        Assert.Equal(new List<Guid> { Trailer.Id }, queue);
    }

    [Fact]
    public async Task GetQueue_Cancelled_Throws()
    {
        _picks.Setup(p => p.GetPickedListsAsync(_user, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, Mock.Of<IDiscoveryFilmSync>(), Mock.Of<IDiscoveryStore>(), Mock.Of<IUserListStore>(), NullLogger<WatchTrailerAction>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.GetQueueAsync(_user, new Movie { Id = Guid.NewGuid() }, Ct));
    }

    [Fact]
    public async Task GetQueueEntries_PickedList_NamesEachTrailerAfterItsFilmInQueueOrder()
    {
        var first = new Movie { Id = Guid.NewGuid(), Name = "The Shawshank Redemption", ProductionYear = 1994 };
        var second = new Movie { Id = Guid.NewGuid(), Name = "Untitled Year", ProductionYear = null };
        var firstTrailer = new Video { Id = Guid.NewGuid(), Name = "Trailer" };
        var secondTrailer = new Video { Id = Guid.NewGuid(), Name = "Trailer" };
        SetTrailers((first.Id, firstTrailer), (second.Id, secondTrailer));
        var list = SetUpPick(first, second);
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, Mock.Of<IDiscoveryFilmSync>(), Mock.Of<IDiscoveryStore>(), Mock.Of<IUserListStore>(), NullLogger<WatchTrailerAction>.Instance);

        var entries = await action.GetQueueEntriesAsync(_user, first, Ct);
        var ids = await action.GetQueueAsync(_user, first, Ct);

        Assert.Equal(
            new[]
            {
                new QueueEntry(firstTrailer.Id, "The Shawshank Redemption", "Trailer \u00b7 1994", first.Id),
                new QueueEntry(secondTrailer.Id, "Untitled Year", "Trailer", second.Id)
            },
            entries);
        Assert.Equal(new[] { firstTrailer.Id, secondTrailer.Id }, ids);
        Assert.NotNull(list);
    }

    [Fact]
    public async Task GetQueueEntries_BorrowedTrailer_NamedAfterTheOwnedFilm()
    {
        var owned = new Movie { Id = Guid.NewGuid(), Name = "Owned Film", ProductionYear = 2001 };
        owned.SetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb, "77");
        var copy = new Movie { Id = Guid.NewGuid(), Name = "Discovery Copy", ProductionYear = 1999 };
        var copyTrailer = new Video { Id = Guid.NewGuid(), Name = "Trailer" };
        SetTrailers((copy.Id, copyTrailer));
        _picks.Setup(p => p.GetPickedListsAsync(_user, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sync = new Mock<IDiscoveryFilmSync>();
        sync.Setup(s => s.FindFilm(77)).Returns(copy);
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, sync.Object, Mock.Of<IDiscoveryStore>(), Mock.Of<IUserListStore>(), NullLogger<WatchTrailerAction>.Instance);

        var entry = Assert.Single(await action.GetQueueEntriesAsync(_user, owned, Ct));

        Assert.Equal(new QueueEntry(copyTrailer.Id, "Owned Film", "Trailer \u00b7 2001", owned.Id), entry);
    }

    [Fact]
    public async Task QueueEntries_CarryFilmId_OwnedWins()
    {
        // A discovery film, then a film the user owns (row item: the owned film, playing its copy's trailer).
        var discovery = new Movie { Id = Guid.NewGuid(), Name = "Discovery Film" };
        var owned = new Movie { Id = Guid.NewGuid(), Name = "Owned Film" };
        var ownedCopy = new Movie { Id = Guid.NewGuid(), Name = "Owned Copy" };
        var discoveryTrailer = new Video { Id = Guid.NewGuid() };
        var copyTrailer = new Video { Id = Guid.NewGuid() };
        SetTrailers((discovery.Id, discoveryTrailer), (ownedCopy.Id, copyTrailer));
        SetUpPick(discovery, owned);
        var sync = new Mock<IDiscoveryFilmSync>();
        sync.Setup(s => s.FindFilm(101)).Returns(ownedCopy);
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, sync.Object, Mock.Of<IDiscoveryStore>(), Mock.Of<IUserListStore>(), NullLogger<WatchTrailerAction>.Instance);

        var entries = await action.GetQueueEntriesAsync(_user, discovery, Ct);

        Assert.Equal(new[] { discoveryTrailer.Id, copyTrailer.Id }, entries.Select(e => e.Id));
        Assert.Equal(new Guid?[] { discovery.Id, owned.Id }, entries.Select(e => e.FilmId));
    }

    [Fact]
    public async Task QueueEntries_DiscoveryCopyOfAnOwnedFilm_CarryTheOwnedFilmId()
    {
        // Opened on the discovery copy itself, outside today's picks: the entry still points at the owned film.
        var copy = new Movie { Id = Guid.NewGuid(), Name = "Copy" };
        copy.SetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb, "88");
        var owned = new Movie { Id = Guid.NewGuid(), Name = "Owned" };
        var trailer = new Video { Id = Guid.NewGuid() };
        SetTrailers((copy.Id, trailer));
        _picks.Setup(p => p.GetPickedListsAsync(_user, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sync = new Mock<IDiscoveryFilmSync>();
        sync.Setup(s => s.FindFilm(88)).Returns(copy);
        sync.Setup(s => s.FindOwned(_user, 88, null)).Returns(owned);
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, sync.Object, Mock.Of<IDiscoveryStore>(), Mock.Of<IUserListStore>(), NullLogger<WatchTrailerAction>.Instance);

        var entry = Assert.Single(await action.GetQueueEntriesAsync(_user, copy, Ct));

        Assert.Equal(trailer.Id, entry.Id);
        Assert.Equal(owned.Id, entry.FilmId);
    }

    private void SetTrailers(params (Guid Owner, Video Trailer)[] trailers)
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => trailers.Where(t => q.OwnerIds.Contains(t.Owner)).Select(t => (BaseItem)t.Trailer).ToList());
        BaseItem.LibraryManager = libraryManager.Object;
    }

    private DiscoveryList SetUpPick(params Movie[] films)
    {
        var list = new DiscoveryList("l", "List", "test", films.Select((f, i) => new DiscoveryFilmRef(100 + i, null)).ToList());
        _picks.Setup(p => p.GetPickedListsAsync(_user, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([list]);
        _planner.Setup(p => p.GetRowItems(_user, list))
            .Returns(films.Select((f, i) => new DiscoveryRowItem(list.Films[i], f)).ToList());
        return list;
    }
}
