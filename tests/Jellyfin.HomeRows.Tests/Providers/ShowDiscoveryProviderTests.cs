using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Providers;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class ShowDiscoveryProviderTests
{
    private static readonly DateTime _now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly _today = DateOnly.FromDateTime(_now.ToLocalTime());

    private readonly Mock<IDiscoveryRowPlanner> _planner = new();
    private readonly Mock<IDiscoveryStore> _store = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly User _user = new("tester", "Default", "Default");

    public ShowDiscoveryProviderTests()
    {
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), It.IsAny<bool>()))
            .Returns<IReadOnlyList<BaseItem>, HomeRowContext, bool>((items, _, _) => items.Select(i => new BaseItemDto { Id = i.Id }).ToArray());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HomeRowContext Context(string? key) => new(_user, new DtoOptions(), _now) { SubRowKey = key };

    private static DiscoveryList List(string id, DiscoveryMediaType type, params int[] tmdbIds)
        => new(id, "Title of " + id, "tests", tmdbIds.Select(t => new DiscoveryFilmRef(t, null, type)).ToArray(), null, type);

    private ShowDiscoveryProvider Create(params DiscoveryList[] stored)
    {
        _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        _store
            .Setup(s => s.GetListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => stored.FirstOrDefault(l => l.Id == id));
        return new ShowDiscoveryProvider(_planner.Object, _store.Object, _adapter.Object, NullLogger<ShowDiscoveryProvider>.Instance);
    }

    [Fact]
    public async Task ShowDiscoveryRow_PicksOnlySeriesLists()
    {
        var shows = List("shows", DiscoveryMediaType.Series, 1);
        var films = List("films", DiscoveryMediaType.Movie, 1);
        var provider = Create(films, shows);
        var show = new Series { Id = Guid.NewGuid() };
        _planner
            .Setup(p => p.GetPickedListsAsync(_user, 3, It.IsAny<DiscoveryPickRules>(), _today, DiscoveryMediaType.Series, It.IsAny<CancellationToken>()))
            .ReturnsAsync([shows]);
        _planner.Setup(p => p.GetRowItems(_user, shows, It.IsAny<bool>())).Returns([new DiscoveryRowItem(shows.Films[0], show)]);

        Assert.Equal(HomeRowProviderIds.ShowDiscovery, provider.Id);
        Assert.Equal("Show discovery", provider.Name);
        Assert.Equal(DiscoveryMediaType.Series, provider.MediaType);
        Assert.Equal(new[] { "shows" }, (await provider.GetSubRowsAsync(Context(null), new JsonObject(), Ct)).Select(r => r.Key));
        Assert.Equal(new[] { "shows" }, await provider.GetResolvableSubRowKeysAsync(Context(null), new JsonObject(), Ct));

        // The parent row and a derived row serve the show list; a derived row naming a film list serves nothing.
        var parent = await provider.GetItemsAsync(Context(null), new JsonObject(), new HomeRowPaging(0, 20), Ct);
        var derived = await provider.GetItemsAsync(Context("shows"), new JsonObject(), new HomeRowPaging(0, 20), Ct);
        var wrongType = await provider.GetItemsAsync(Context("films"), new JsonObject(), new HomeRowPaging(0, 20), Ct);
        Assert.Equal(show.Id, Assert.Single(parent.Items).Id);
        Assert.Equal(show.Id, Assert.Single(derived.Items).Id);
        Assert.Equal("shows", derived.Presentation.PageListId);
        Assert.Empty(wrongType.Items);
        _planner.Verify(p => p.GetRowItems(_user, films, It.IsAny<bool>()), Times.Never);

        // Shown is logged as shows; the Movie pick is never asked for.
        _store.Verify(s => s.MarkShownAsync(It.Is<IEnumerable<int>>(i => i.Single() == 1), DiscoveryMediaType.Series, _today, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _store.Verify(s => s.MarkShownAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        _planner.Verify(p => p.GetPickedListsAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<DiscoveryPickRules>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MovieDiscoveryRow_IgnoresSeriesLists()
    {
        var shows = List("shows", DiscoveryMediaType.Series, 1);
        var films = List("films", DiscoveryMediaType.Movie, 1);
        _store.Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([films, shows]);
        _store.Setup(s => s.GetListAsync("shows", It.IsAny<CancellationToken>())).ReturnsAsync(shows);
        var provider = new MovieDiscoveryProvider(_planner.Object, _store.Object, _adapter.Object, NullLogger<MovieDiscoveryProvider>.Instance);

        Assert.Equal(new[] { "films" }, await provider.GetResolvableSubRowKeysAsync(Context(null), new JsonObject(), Ct));
        Assert.Empty((await provider.GetItemsAsync(Context("shows"), new JsonObject(), new HomeRowPaging(0, 20), Ct)).Items);
        _planner.Verify(p => p.GetRowItems(_user, shows, It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Schema_SameSettingsAsMovieDiscovery_ShowWording()
    {
        var shows = await Create().GetSchemaAsync(Context(null), Ct);
        var films = await new MovieDiscoveryProvider(_planner.Object, _store.Object, _adapter.Object, NullLogger<MovieDiscoveryProvider>.Instance).GetSchemaAsync(Context(null), Ct);

        Assert.Equal(films.Settings.Select(s => (s.Key, s.Type, s.Default?.ToJsonString())), shows.Settings.Select(s => (s.Key, s.Type, s.Default?.ToJsonString())));
        Assert.Equal("Only shows you can play", shows.Settings[2].Title);
        Assert.Equal("Only films you can play", films.Settings[2].Title);
        Assert.Equal("Films a list needs", films.Settings[3].Title);
    }

    [Fact]
    public async Task Settings_ShowRowsByType()
    {
        var configuration = new Mock<IHomeRowConfigurationService>();
        var showRow = new EffectiveHomeRow(new HomeRowInstance(Guid.NewGuid(), HomeRowProviderIds.ShowDiscovery, null, new JsonObject { ["externalTrailers"] = true, ["minReadyFilms"] = 9 }, true), true);
        configuration
            .Setup(c => c.GetEffectiveLayoutAsync(_user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveHomeRowLayout(HomeRowLayoutSource.User, [showRow]));
        configuration.Setup(c => c.GetAdminDefaultAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HomeRowInstance>?)null);
        configuration.Setup(c => c.GetUserLayoutsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([[showRow.Row]]);
        var settings = new MovieDiscoverySettings(configuration.Object);

        Assert.True(await settings.GetExternalTrailersAsync(_user, DiscoveryMediaType.Series, Ct));
        Assert.False(await settings.GetExternalTrailersAsync(_user, Ct));
        Assert.Equal(9, await settings.GetReadyThresholdAsync(DiscoveryMediaType.Series, Ct));
        Assert.Equal(DiscoveryPickRules.DefaultMinReadyFilms, await settings.GetReadyThresholdAsync(Ct));
    }
}
