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
using MediaBrowser.Controller.Entities.TV;
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
    private readonly Mock<IDiscoveryRequestStore> _requests = new();
    private readonly Mock<ISeasonStore> _seasons = new();
    private readonly Mock<IUserListStore> _userLists = new();
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
            _requests.Object,
            _seasons.Object,
            _userLists.Object,
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

    [Fact]
    public async Task SeasonRequests_RouteShapes()
    {
        _seasons.Setup(s => s.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SeasonRequest(200, 1, SeasonRequest.StatePending, null, DateTime.UtcNow),
            new SeasonRequest(100, 4, SeasonRequest.StatePending, null, DateTime.UtcNow),
            new SeasonRequest(100, 2, SeasonRequest.StatePending, null, DateTime.UtcNow),
        ]);
        _sync.Setup(s => s.FindOwnedSeries(100, null)).Returns(new Series { Name = "Owned Show", Path = "/tv/Owned Show" });
        _sync.Setup(s => s.FindSeries(200)).Returns(new Series { Name = "Discovery Show" });

        var result = await _subject.GetSeasonRequests(SeasonRequest.StatePending, CancellationToken.None);

        // One entry per series, its seasons ascending; the owned show has its folder, the discovery-only one none.
        var groups = Assert.IsType<DiscoverySeasonRequestDto[]>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(new[] { 100, 200 }, groups.Select(g => g.SeriesTmdbId));
        Assert.Equal(new[] { 2, 4 }, groups[0].SeasonNumbers);
        Assert.Equal("/tv/Owned Show", groups[0].SeriesPath);
        Assert.Equal("Owned Show", groups[0].Name);
        Assert.Equal(new[] { 1 }, groups[1].SeasonNumbers);
        Assert.Null(groups[1].SeriesPath);
        Assert.Equal("Discovery Show", groups[1].Name);

        Assert.IsType<BadRequestObjectResult>((await _subject.GetSeasonRequests("bogus", CancellationToken.None)).Result);

        // The app reports a season handled: 204, stored without the request token.
        _seasons.Setup(s => s.GetAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new SeasonRequest(100, 2, SeasonRequest.StatePending, null, DateTime.UtcNow)]);
        _seasons.Setup(s => s.GetAsync(300, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Assert.IsType<NoContentResult>(await _subject.SetSeasonRequestState(100, 2, new DiscoverySeasonRequestUpdateDto(SeasonRequest.StateFailed, "No root folder"), CancellationToken.None));
        _seasons.Verify(s => s.SetStateAsync(100, 2, SeasonRequest.StateFailed, "No root folder", CancellationToken.None), Times.Once);

        // No such row: 404, nothing stored.
        Assert.IsType<NotFoundResult>(await _subject.SetSeasonRequestState(100, 3, new DiscoverySeasonRequestUpdateDto(SeasonRequest.StateHandled, null), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await _subject.SetSeasonRequestState(300, 1, new DiscoverySeasonRequestUpdateDto(SeasonRequest.StateHandled, null), CancellationToken.None));
        _seasons.Verify(s => s.SetStateAsync(It.IsAny<int>(), 3, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // The Sonarr report replaces every status at once.
        var report = new DiscoverySonarrSeasonsDto([new DiscoverySonarrSeasonDto(100, 2, SonarrSeasonStatus.StatusDownloading, false), new DiscoverySonarrSeasonDto(200, 1, SonarrSeasonStatus.StatusIdle, true)]);
        Assert.IsType<NoContentResult>(await _subject.SetSonarrSeasons(report, CancellationToken.None));
        _seasons.Verify(
            s => s.ReplaceSonarrStatusAsync(
                It.Is<IReadOnlyList<SonarrSeasonStatus>>(l => l.SequenceEqual(new[] { new SonarrSeasonStatus(100, 2, SonarrSeasonStatus.StatusDownloading, false), new SonarrSeasonStatus(200, 1, SonarrSeasonStatus.StatusIdle, true) })),
                CancellationToken.None),
            Times.Once);
    }

    [Theory]
    [InlineData(SeasonRequest.StatePending)]
    [InlineData("downloading")]
    [InlineData("")]
    [InlineData(null)]
    public async Task PutSeasonRequest_RejectsUnknownState(string? state)
    {
        _seasons.Setup(s => s.GetAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new SeasonRequest(100, 2, SeasonRequest.StatePending, null, DateTime.UtcNow)]);

        var result = await _subject.SetSeasonRequestState(100, 2, new DiscoverySeasonRequestUpdateDto(state, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _seasons.Verify(s => s.SetStateAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Wanted")]
    [InlineData(null)]
    public async Task PutSonarrSeasons_RejectsUnknownStatus(string? status)
    {
        var report = new DiscoverySonarrSeasonsDto([new DiscoverySonarrSeasonDto(100, 1, SonarrSeasonStatus.StatusWanted, false), new DiscoverySonarrSeasonDto(100, 2, status, false)]);

        var result = await _subject.SetSonarrSeasons(report, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<BadRequestObjectResult>(await _subject.SetSonarrSeasons(new DiscoverySonarrSeasonsDto(null), CancellationToken.None));
        _seasons.Verify(s => s.ReplaceSonarrStatusAsync(It.IsAny<IReadOnlyList<SonarrSeasonStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Protected_IncludesSeriesWithPendingSeasons()
    {
        _requests.Setup(r => r.GetAllAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new DiscoveryRequest(500, DiscoveryRequest.StateRequested, DateTime.UtcNow, [], DiscoveryMediaType.Series)]);
        _userLists.Setup(l => l.GetListedTmdbIdsAsync(It.IsAny<DiscoveryMediaType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscoveryMediaType t, CancellationToken _) => t == DiscoveryMediaType.Movie ? new HashSet<int> { 7 } : new HashSet<int>());
        _seasons.Setup(s => s.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SeasonRequest(300, 1, SeasonRequest.StatePending, null, DateTime.UtcNow),
            new SeasonRequest(300, 2, SeasonRequest.StatePending, null, DateTime.UtcNow),
            new SeasonRequest(500, 1, SeasonRequest.StatePending, null, DateTime.UtcNow),
        ]);

        var result = await _subject.GetProtected(CancellationToken.None);

        // Each series once, whether it has a series request, pending seasons or both; films unchanged.
        var dto = Assert.IsType<DiscoveryProtectedDto>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(
            new[] { new DiscoveryProtectedItemDto(7, DiscoveryMediaType.Movie), new DiscoveryProtectedItemDto(300, DiscoveryMediaType.Series), new DiscoveryProtectedItemDto(500, DiscoveryMediaType.Series) },
            dto.Items);
        Assert.Equal(new[] { 7 }, dto.TmdbIds);
    }
}
