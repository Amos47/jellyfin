using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Providers;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class MovieDiscoveryProviderTests
{
    // 2026-11-07 12:00 UTC; the provider works out the server's local date from it.
    private static readonly DateTime _now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly _today = DateOnly.FromDateTime(_now.ToLocalTime());

    private readonly Mock<IDiscoveryRowPlanner> _planner = new();
    private readonly Mock<IDiscoveryStore> _store = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly User _user = new("tester", "Default", "Default");
    private readonly List<IReadOnlyList<BaseItem>> _converted = [];
    private readonly List<(int[] TmdbIds, DateOnly Day)> _shown = [];

    public MovieDiscoveryProviderTests()
    {
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((items, _, _) => _converted.Add(items))
            .Returns<IReadOnlyList<BaseItem>, HomeRowContext, bool>((items, _, _) => items.Select(i => new BaseItemDto { Id = i.Id }).ToArray());
        _store
            .Setup(s => s.MarkShownAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<int>, DateOnly, CancellationToken>((ids, day, _) => _shown.Add((ids.ToArray(), day)))
            .Returns(Task.CompletedTask);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HomeRowContext Context(int subRow = 0) => new(_user, new DtoOptions(), _now) { SubRow = subRow };

    private MovieDiscoveryProvider CreateProvider(params DiscoveryList[] picked)
    {
        _planner
            .Setup(p => p.GetPickedListsAsync(_user, It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns<User, int, DateOnly, CancellationToken>((_, max, _, _) => Task.FromResult<IReadOnlyList<DiscoveryList>>(picked.Take(max).ToArray()));
        return new MovieDiscoveryProvider(_planner.Object, _store.Object, _adapter.Object, NullLogger<MovieDiscoveryProvider>.Instance);
    }

    private void SetRowItems(DiscoveryList list, params DiscoveryRowItem[] items)
        => _planner.Setup(p => p.GetRowItems(_user, list)).Returns(items);

    private static DiscoveryList List(string id, params int[] tmdbIds)
        => new(id, "Title of " + id, "tests", tmdbIds.Select(t => new DiscoveryFilmRef(t, null)).ToArray());

    private static DiscoveryRowItem Item(int tmdbId)
        => new(new DiscoveryFilmRef(tmdbId, null), new Movie { Id = Guid.NewGuid(), Name = "Film " + tmdbId });

    [Fact]
    public async Task Schema_MaxRowsAndCardStyle()
    {
        var provider = CreateProvider();

        var schema = await provider.GetSchemaAsync(Context(), Ct);

        Assert.Equal(HomeRowProviderIds.MovieDiscovery, provider.Id);
        Assert.IsAssignableFrom<IExpandingHomeRowProvider>(provider);
        Assert.Equal(["maxRows", "cardStyle"], schema.Settings.Select(s => s.Key));
        var maxRows = schema.Settings[0];
        Assert.Equal(HomeRowSettingType.Integer, maxRows.Type);
        Assert.Equal(1, maxRows.Minimum);
        Assert.Equal(10, maxRows.Maximum);
        Assert.Equal(3, maxRows.Default!.GetValue<int>());
        Assert.Equal("poster", schema.Settings[1].Default!.GetValue<string>());
    }

    [Fact]
    public async Task RowCount_IsPickedListCount_CappedAtMaxRows()
    {
        var provider = CreateProvider(List("a", 1), List("b", 2), List("c", 3));

        var count = await provider.GetRowCountAsync(Context(), new JsonObject { ["maxRows"] = 2 }, Ct);
        var none = await CreateProvider().GetRowCountAsync(Context(), new JsonObject { ["maxRows"] = 3 }, Ct);

        Assert.Equal(2, count);
        Assert.Equal(0, none);
        _planner.Verify(p => p.GetPickedListsAsync(_user, 2, _today, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task RowCount_MissingMaxRows_UsesDefaultThree()
    {
        var provider = CreateProvider(List("a", 1), List("b", 2), List("c", 3), List("d", 4));

        Assert.Equal(3, await provider.GetRowCountAsync(Context(), new JsonObject(), Ct));
    }

    [Fact]
    public async Task Items_SubRow_ReturnsListTitleAndOrder()
    {
        var a = List("a", 1);
        var b = List("b", 20, 21, 22);
        var provider = CreateProvider(a, b);
        var items = new[] { Item(20), Item(21), Item(22) };
        SetRowItems(b, items);

        var result = await provider.GetItemsAsync(Context(subRow: 1), new JsonObject { ["maxRows"] = 3 }, new HomeRowPaging(0, 20), Ct);

        Assert.Equal("Title of b", result.Title);
        Assert.Equal(items.Select(i => i.Item.Id), result.Items.Select(i => i.Id));
        Assert.Equal(3, result.TotalRecordCount);
        Assert.Null(result.Error);
        Assert.Equal(HomeRowCardShape.Poster, result.Presentation.CardShape);
    }

    [Fact]
    public async Task Items_Paging_AppliesToListOrder()
    {
        var a = List("a", 1, 2, 3, 4);
        var provider = CreateProvider(a);
        var items = new[] { Item(1), Item(2), Item(3), Item(4) };
        SetRowItems(a, items);

        var result = await provider.GetItemsAsync(Context(), new JsonObject(), new HomeRowPaging(1, 2), Ct);

        Assert.Equal(new[] { items[1].Item.Id, items[2].Item.Id }, result.Items.Select(i => i.Id));
        Assert.Equal(4, result.TotalRecordCount);
    }

    [Fact]
    public async Task Items_SubRowBeyondPicks_Empty()
    {
        var provider = CreateProvider(List("a", 1));

        var result = await provider.GetItemsAsync(Context(subRow: 1), new JsonObject(), new HomeRowPaging(0, 20), Ct);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task Items_RecordsShown()
    {
        var a = List("a", 5, 6, 7);
        var provider = CreateProvider(a);
        SetRowItems(a, Item(5), Item(6), Item(7));

        await provider.GetItemsAsync(Context(), new JsonObject(), new HomeRowPaging(0, 2), Ct);

        var shown = Assert.Single(_shown);
        Assert.Equal([5, 6], shown.TmdbIds);
        Assert.Equal(_today, shown.Day);
    }

    [Fact]
    public async Task Items_RecordingShownFails_StillReturnsRow()
    {
        var a = List("a", 5, 6);
        var provider = CreateProvider(a);
        var items = new[] { Item(5), Item(6) };
        SetRowItems(a, items);
        _store
            .Setup(s => s.MarkShownAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("custom-jelly.db is locked"));

        var result = await provider.GetItemsAsync(Context(), new JsonObject(), new HomeRowPaging(0, 20), Ct);

        Assert.Equal("Title of a", result.Title);
        Assert.Equal(items.Select(i => i.Item.Id), result.Items.Select(i => i.Id));
        Assert.Equal(2, result.TotalRecordCount);
    }

    [Fact]
    public async Task Items_RecordingShownCancelled_Throws()
    {
        var a = List("a", 5);
        var provider = CreateProvider(a);
        SetRowItems(a, Item(5));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _store
            .Setup(s => s.MarkShownAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetItemsAsync(Context(), new JsonObject(), new HomeRowPaging(0, 20), cts.Token));
    }

    [Fact]
    public async Task Items_PastTheEnd_RecordsNothing()
    {
        var a = List("a", 5);
        var provider = CreateProvider(a);
        SetRowItems(a, Item(5));

        var result = await provider.GetItemsAsync(Context(), new JsonObject(), new HomeRowPaging(5, 2), Ct);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.TotalRecordCount);
        Assert.Empty(_shown);
    }
}
