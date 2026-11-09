using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.DiscoveryDtos;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

/// <summary>
/// Fork: discovery. The picks survive one user's broken layout, and a list change always syncs the films.
/// </summary>
public class DiscoveryControllerTests
{
    private static readonly DateOnly _day = new(2026, 11, 8);

    private readonly Mock<IDiscoveryStore> _store = new();
    private readonly Mock<IDiscoveryFilmSync> _sync = new();
    private readonly Mock<IDiscoveryTrailers> _trailers = new();
    private readonly Mock<IDiscoveryUserPicks> _picks = new();
    private readonly Mock<IDiscoveryRowPlanner> _planner = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly DiscoveryController _subject;

    public DiscoveryControllerTests()
    {
        _subject = new DiscoveryController(
            _store.Object,
            _sync.Object,
            _trailers.Object,
            _picks.Object,
            _planner.Object,
            _userManager.Object,
            NullLogger<DiscoveryController>.Instance);
    }

    [Fact]
    public async Task GetPicks_OneUsersLayoutFails_SkipsThatUser()
    {
        var broken = new User("broken", "Default", "Default");
        var fine = new User("fine", "Default", "Default");
        _userManager.Setup(m => m.GetUsers()).Returns([broken, fine]);
        _picks.Setup(p => p.GetPickedListsAsync(broken, _day, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broken layout"));
        _picks.Setup(p => p.GetPickedListsAsync(fine, _day, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DiscoveryList("list", "List", "tests", [new DiscoveryFilmRef(7, null)])]);

        var result = await _subject.GetPicks(_day, CancellationToken.None);

        var picks = Assert.IsType<DiscoveryPicksDto>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal([7], picks.TmdbIds);
    }

    [Fact]
    public async Task GetPicks_Cancelled_Throws()
    {
        // A cancelled request is not a broken layout: it is not swallowed.
        var user = new User("user", "Default", "Default");
        _userManager.Setup(m => m.GetUsers()).Returns([user]);
        _picks.Setup(p => p.GetPickedListsAsync(user, _day, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _subject.GetPicks(_day, CancellationToken.None));
    }

    [Fact]
    public async Task SetList_SyncsWithoutTheRequestToken()
    {
        // Once the list is saved, a client hanging up must not stop the films following it.
        using var request = new CancellationTokenSource();

        var result = await _subject.SetList(
            "list",
            new DiscoveryListUpdateDto("List", "tests", [new DiscoveryFilmDto(7, null)]),
            request.Token);

        Assert.IsType<NoContentResult>(result);
        _sync.Verify(s => s.SyncAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task DeleteList_SyncsWithoutTheRequestToken()
    {
        using var request = new CancellationTokenSource();
        _store.Setup(s => s.RemoveListAsync("list", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _subject.DeleteList("list", request.Token);

        Assert.IsType<NoContentResult>(result);
        _sync.Verify(s => s.SyncAsync(CancellationToken.None), Times.Once);
    }
}
