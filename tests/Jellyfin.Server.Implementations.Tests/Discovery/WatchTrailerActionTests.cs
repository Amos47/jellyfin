using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, Mock.Of<IDiscoveryFilmSync>(), NullLogger<WatchTrailerAction>.Instance);

        var queue = await action.GetQueueAsync(_user, new Movie { Id = Guid.NewGuid() }, Ct);

        Assert.Equal(new List<Guid> { Trailer.Id }, queue);
    }

    [Fact]
    public async Task GetQueue_Cancelled_Throws()
    {
        _picks.Setup(p => p.GetPickedListsAsync(_user, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var action = new WatchTrailerAction(_picks.Object, _planner.Object, Mock.Of<IDiscoveryFilmSync>(), NullLogger<WatchTrailerAction>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.GetQueueAsync(_user, new Movie { Id = Guid.NewGuid() }, Ct));
    }
}
