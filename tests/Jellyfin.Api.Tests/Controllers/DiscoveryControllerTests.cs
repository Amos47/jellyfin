using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.DiscoveryDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
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
    private readonly Mock<IDiscoveryUserSettings> _settings = new();
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
            _settings.Object,
            Mock.Of<IDiscoveryRequestStore>(),
            Mock.Of<IUserListStore>(),
            _userManager.Object,
            NullLogger<DiscoveryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        _settings.Setup(s => s.GetReadyThresholdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(DiscoveryPickRules.DefaultMinReadyFilms);
    }

    private User Admin()
    {
        var admin = new User("admin", "Default", "Default");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        _userManager.Setup(m => m.GetUsers()).Returns([admin]);
        return admin;
    }

    private static DiscoveryList List(string id, params int[] tmdbIds)
        => new(id, id, "tests", tmdbIds.Select(t => new DiscoveryFilmRef(t, null)).ToArray());

    private async Task<DiscoveryListDto[]> GetListsAsync()
    {
        var result = await _subject.GetLists(CancellationToken.None);
        return Assert.IsType<DiscoveryListDto[]>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task GetLists_FilmInTwoLists_ResolvedOnce()
    {
        var admin = Admin();
        _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([List("a", 1, 2), List("b", 2, 3)]);
        _planner.Setup(p => p.IsPlayable(admin, It.IsAny<DiscoveryFilmRef>())).Returns<User, DiscoveryFilmRef>((_, f) => f.TmdbId != 3);

        var lists = await GetListsAsync();

        Assert.Equal(new[] { 2, 1 }, lists.Select(l => l.ReadyFilms));
        _planner.Verify(p => p.IsPlayable(admin, It.Is<DiscoveryFilmRef>(f => f.TmdbId == 2)), Times.Once);
        _planner.Verify(p => p.IsPlayable(It.IsAny<User>(), It.IsAny<DiscoveryFilmRef>()), Times.Exactly(3));
    }

    [Fact]
    public async Task GetLists_Ready_UsesTheThreshold()
    {
        var admin = Admin();
        _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([List("two", 1, 2), List("three", 1, 2, 3), List("one", 1)]);
        _planner.Setup(p => p.IsPlayable(admin, It.IsAny<DiscoveryFilmRef>())).Returns(true);
        _settings.Setup(s => s.GetReadyThresholdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var lists = await GetListsAsync();

        Assert.Equal(new[] { true, true, false }, lists.Select(l => l.Ready));
    }

    [Fact]
    public async Task ReshufflePicks_InvalidatesReadiness()
    {
        await _subject.ReshufflePicks(null, CancellationToken.None);

        _planner.Verify(p => p.InvalidateReadiness(), Times.Once);
    }

    [Fact]
    public async Task ListAndTrailerChanges_InvalidateReadiness()
    {
        _store.Setup(s => s.RemoveListAsync("list", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _trailers.Setup(t => t.SetAsync(7, "/t.mkv", It.IsAny<CancellationToken>())).ReturnsAsync(DiscoveryTrailerResult.Set);

        await _subject.SetList("list", new DiscoveryListUpdateDto("List", "tests", [new DiscoveryFilmDto(7, null)]), CancellationToken.None);
        _planner.Verify(p => p.InvalidateReadiness(), Times.Exactly(1));
        await _subject.DeleteList("list", CancellationToken.None);
        _planner.Verify(p => p.InvalidateReadiness(), Times.Exactly(2));
        await _subject.SetTrailer(7, new DiscoveryTrailerUpdateDto("/t.mkv"), CancellationToken.None);
        _planner.Verify(p => p.InvalidateReadiness(), Times.Exactly(3));
        await _subject.DeleteTrailer(7, CancellationToken.None);
        _planner.Verify(p => p.InvalidateReadiness(), Times.Exactly(4));
    }

    [Fact]
    public async Task SetList_UndefinedItemType_BadRequest_NothingStored()
    {
        var result = await _subject.SetList(
            "list",
            new DiscoveryListUpdateDto("List", "tests", [new DiscoveryFilmDto(7, null)], ItemType: (DiscoveryMediaType)7),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Verify(s => s.SetListAsync(It.IsAny<DiscoveryList>(), It.IsAny<CancellationToken>()), Times.Never);
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
