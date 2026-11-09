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
    private readonly List<DiscoveryList> _stored = [];

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

    private HomeRowContext Context(string? key = "a") => new(_user, new DtoOptions(), _now) { SubRowKey = key };

    // The picked lists are also in the store, with any extra lists.
    private MovieDiscoveryProvider CreateProvider(params DiscoveryList[] picked)
        => CreateProvider(picked, []);

    private MovieDiscoveryProvider CreateProvider(DiscoveryList[] picked, DiscoveryList[] alsoStored)
    {
        _planner
            .Setup(p => p.GetPickedListsAsync(_user, It.IsAny<int>(), It.IsAny<DiscoveryPickRules>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns<User, int, DiscoveryPickRules, DateOnly, CancellationToken>((_, max, _, _, _) => Task.FromResult<IReadOnlyList<DiscoveryList>>(picked.Take(max).ToArray()));
        _stored.Clear();
        _stored.AddRange(picked.Concat(alsoStored).OrderBy(l => l.Id, StringComparer.Ordinal));
        _store
            .Setup(s => s.GetListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored.ToArray());
        _store
            .Setup(s => s.GetListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => _stored.FirstOrDefault(l => l.Id == id));
        return new MovieDiscoveryProvider(_planner.Object, _store.Object, _adapter.Object, NullLogger<MovieDiscoveryProvider>.Instance);
    }

    private void SetRowItems(DiscoveryList list, params DiscoveryRowItem[] items)
        => _planner.Setup(p => p.GetRowItems(_user, list, It.IsAny<bool>())).Returns(items);

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
        Assert.Equal(["maxRows", "externalTrailers", "requireTrailer", "minReadyFilms", "cardStyle"], schema.Settings.Select(s => s.Key));
        var maxRows = schema.Settings[0];
        Assert.Equal(HomeRowSettingType.Integer, maxRows.Type);
        Assert.Equal(1, maxRows.Minimum);
        Assert.Equal(10, maxRows.Maximum);
        Assert.Equal(3, maxRows.Default!.GetValue<int>());
        var externalTrailers = schema.Settings[1];
        Assert.Equal(HomeRowSettingType.Boolean, externalTrailers.Type);
        Assert.False(externalTrailers.Default!.GetValue<bool>());
        var requireTrailer = schema.Settings[2];
        Assert.Equal(HomeRowSettingType.Boolean, requireTrailer.Type);
        Assert.True(requireTrailer.Default!.GetValue<bool>());
        var minReadyFilms = schema.Settings[3];
        Assert.Equal(HomeRowSettingType.Integer, minReadyFilms.Type);
        Assert.Equal(1, minReadyFilms.Minimum);
        Assert.Equal(20, minReadyFilms.Maximum);
        Assert.Equal(5, minReadyFilms.Default!.GetValue<int>());
        Assert.Equal("poster", schema.Settings[4].Default!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public async Task Config_RejectsBadMinReadyFilms(int value)
    {
        var schema = await CreateProvider().GetSchemaAsync(Context(), Ct);

        var bad = HomeRowConfigValidator.Validate(schema, new JsonObject { ["minReadyFilms"] = value });
        var notANumber = HomeRowConfigValidator.Validate(schema, new JsonObject { ["minReadyFilms"] = "five" });
        var badBool = HomeRowConfigValidator.Validate(schema, new JsonObject { ["requireTrailer"] = "yes" });
        var good = HomeRowConfigValidator.Validate(schema, new JsonObject { ["minReadyFilms"] = 20, ["requireTrailer"] = false });
        var missing = HomeRowConfigValidator.Validate(schema, new JsonObject());

        Assert.Single(bad.Errors, e => e.Key == "minReadyFilms");
        Assert.Single(notANumber.Errors, e => e.Key == "minReadyFilms");
        Assert.Single(badBool.Errors, e => e.Key == "requireTrailer");
        Assert.Empty(good.Errors);
        Assert.Empty(missing.Errors);
        Assert.Equal(5, missing.Config["minReadyFilms"]!.GetValue<int>());
        Assert.True(missing.Config["requireTrailer"]!.GetValue<bool>());
    }

    [Fact]
    public void Rules_ReadFromConfig_DefaultsAndClamps()
    {
        Assert.Equal(DiscoveryPickRules.Default, MovieDiscoveryProvider.GetPickRules(new JsonObject()));
        Assert.Equal(new DiscoveryPickRules(false, 7), MovieDiscoveryProvider.GetPickRules(new JsonObject { ["requireTrailer"] = false, ["minReadyFilms"] = 7 }));
        Assert.Equal(new DiscoveryPickRules(true, 20), MovieDiscoveryProvider.GetPickRules(new JsonObject { ["minReadyFilms"] = 99 }));
        Assert.Equal(new DiscoveryPickRules(true, 1), MovieDiscoveryProvider.GetPickRules(new JsonObject { ["minReadyFilms"] = 0 }));
        Assert.Equal(DiscoveryPickRules.Default, MovieDiscoveryProvider.GetPickRules(new JsonObject { ["requireTrailer"] = "no", ["minReadyFilms"] = "x" }));
    }

    [Fact]
    public async Task SubRowsAndItems_PassTheRowRules()
    {
        var a = List("a", 1);
        var provider = CreateProvider(a);
        SetRowItems(a, Item(1));
        var config = new JsonObject { ["maxRows"] = 2, ["requireTrailer"] = false, ["minReadyFilms"] = 3 };

        await provider.GetSubRowsAsync(Context(null), config, Ct);
        await provider.GetItemsAsync(Context("a"), config, new HomeRowPaging(0, 20), Ct);
        await provider.GetItemsAsync(Context("a"), new JsonObject(), new HomeRowPaging(0, 20), Ct);

        _planner.Verify(p => p.GetPickedListsAsync(_user, 2, new DiscoveryPickRules(false, 3), _today, It.IsAny<CancellationToken>()));
        _planner.Verify(p => p.GetRowItems(_user, a, false));
        _planner.Verify(p => p.GetRowItems(_user, a, true));
        _planner.Verify(p => p.GetRowItems(_user, a), Times.Never);
    }

    [Fact]
    public async Task ConfigValidation_RejectsNonBoolExternalTrailers()
    {
        var schema = await CreateProvider().GetSchemaAsync(Context(), Ct);

        var bad = HomeRowConfigValidator.Validate(schema, new JsonObject { ["externalTrailers"] = "yes" });
        var good = HomeRowConfigValidator.Validate(schema, new JsonObject { ["externalTrailers"] = true });
        var missing = HomeRowConfigValidator.Validate(schema, new JsonObject());

        Assert.Single(bad.Errors, e => e.Key == "externalTrailers");
        Assert.Empty(good.Errors);
        Assert.True(good.Config["externalTrailers"]!.GetValue<bool>());
        Assert.Empty(missing.Errors);
        Assert.False(missing.Config["externalTrailers"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SubRows_ArePickedListIds_InPickOrder_CappedAtMaxRows()
    {
        var provider = CreateProvider(List("c", 3), List("a", 1), List("b", 2));

        var rows = await provider.GetSubRowsAsync(Context(null), new JsonObject { ["maxRows"] = 2 }, Ct);
        var none = await CreateProvider().GetSubRowsAsync(Context(null), new JsonObject { ["maxRows"] = 3 }, Ct);

        Assert.Equal(new[] { "c", "a" }, rows.Select(r => r.Key));
        Assert.Empty(none);
        _planner.Verify(p => p.GetPickedListsAsync(_user, 2, DiscoveryPickRules.Default, _today, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task SubRows_MissingMaxRows_UsesDefaultThree()
    {
        var provider = CreateProvider(List("a", 1), List("b", 2), List("c", 3), List("d", 4));

        Assert.Equal(3, (await provider.GetSubRowsAsync(Context(null), new JsonObject(), Ct)).Count);
    }

    [Fact]
    public async Task ResolvableKeys_AreEveryStoredListId()
    {
        var provider = CreateProvider([List("a", 1)], [List("b", 2), List("c", 3)]);

        var keys = await provider.GetResolvableSubRowKeysAsync(Context(null), new JsonObject { ["maxRows"] = 1 }, Ct);

        Assert.Equal(new[] { "a", "b", "c" }, keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Items_KeyNotPickedToday_ServesThatList()
    {
        var a = List("a", 1);
        var retired = List("retired", 30, 31);
        var provider = CreateProvider([a], [retired]);
        var items = new[] { Item(30), Item(31) };
        SetRowItems(retired, items);

        var result = await provider.GetItemsAsync(Context("retired"), new JsonObject { ["maxRows"] = 1 }, new HomeRowPaging(0, 20), Ct);

        Assert.Equal("Title of retired", result.Title);
        Assert.Equal(items.Select(i => i.Item.Id), result.Items.Select(i => i.Id));
        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal([30, 31], Assert.Single(_shown).TmdbIds);
    }

    [Fact]
    public async Task Items_NoKey_ServesFirstPick()
    {
        // The parent row's own id (no sub-row): today's first pick, as before.
        var a = List("a", 1);
        var b = List("b", 2);
        var provider = CreateProvider(b, a);
        SetRowItems(b, Item(2));

        var result = await provider.GetItemsAsync(Context(null), new JsonObject(), new HomeRowPaging(0, 20), Ct);

        Assert.Equal("Title of b", result.Title);
    }

    [Fact]
    public async Task Items_Key_ReturnsListTitleAndOrder()
    {
        var a = List("a", 1);
        var b = List("b", 20, 21, 22);
        var provider = CreateProvider(a, b);
        var items = new[] { Item(20), Item(21), Item(22) };
        SetRowItems(b, items);

        var result = await provider.GetItemsAsync(Context("b"), new JsonObject { ["maxRows"] = 3 }, new HomeRowPaging(0, 20), Ct);

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
    public async Task Items_UnknownKeySentinel_EmptyWithoutStoreLookup()
    {
        var provider = CreateProvider(List("a", 1));

        var result = await provider.GetItemsAsync(Context(HomeSubRow.UnknownKey), new JsonObject(), new HomeRowPaging(0, 20), Ct);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
        _store.Verify(s => s.GetListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Items_UnknownKey_Empty()
    {
        var provider = CreateProvider(List("a", 1));

        var result = await provider.GetItemsAsync(Context("deleted"), new JsonObject(), new HomeRowPaging(0, 20), Ct);

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

    [Fact]
    public async Task DerivedRows_CarryPageListId()
    {
        var a = List("a", 5);
        var b = List("b", 6);
        var provider = CreateProvider([a], [b]);
        SetRowItems(a, Item(5));
        SetRowItems(b, Item(6));

        var derived = await provider.GetItemsAsync(Context("b"), new JsonObject(), new HomeRowPaging(0, 20), Ct);
        var parent = await provider.GetItemsAsync(Context(null), new JsonObject(), new HomeRowPaging(0, 20), Ct);
        var gone = await provider.GetItemsAsync(Context(HomeSubRow.UnknownKey), new JsonObject(), new HomeRowPaging(0, 20), Ct);

        // Each row names the list page it opens; the card style is unchanged.
        Assert.Equal("b", derived.Presentation.PageListId);
        Assert.Equal(HomeRowCardShape.Poster, derived.Presentation.CardShape);
        Assert.Equal("a", parent.Presentation.PageListId);
        Assert.Null(gone.Presentation.PageListId);
    }
}
