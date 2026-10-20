using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Providers.NewReleases;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class NewReleasesTests
{
    private static readonly DateTime _now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _svu = Guid.NewGuid();
    private static readonly Guid _office = Guid.NewGuid();

    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(), _now);

    private InternalItemsQuery? _query;
    private IReadOnlyList<BaseItem>? _adapted;

    private static Episode Episode(string name, Guid series, int season, int number, DateTime premiere)
        => new() { Id = Guid.NewGuid(), Name = name, SeriesId = series, ParentIndexNumber = season, IndexNumber = number, PremiereDate = premiere };

    private static Movie MovieItem(string name, DateTime premiere)
        => new() { Id = Guid.NewGuid(), Name = name, PremiereDate = premiere };

    private static string[] Names(IEnumerable<BaseItem> items) => items.Select(i => i.Name).ToArray();

    private NewReleasesProvider CreateProvider(BaseItem[]? items = null)
    {
        items ??= [];
        _libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => _query = q)
            .Returns(new QueryResult<BaseItem>(0, items.Length, items));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), false))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((i, _, _) => _adapted = i)
            .Returns(Array.Empty<BaseItemDto>());
        return new NewReleasesProvider(_libraryManager.Object, _adapter.Object);
    }

    [Fact]
    public void Collapse_OneCardPerShowEarliestNewEpisode()
    {
        var e6 = Episode("SVU S26E06", _svu, 26, 6, _now.AddDays(-2));
        var e5 = Episode("SVU S26E05", _svu, 26, 5, _now.AddDays(-9));

        Assert.Equal(["SVU S26E05"], Names(NewReleasesCollapser.Collapse([e6, e5])));
    }

    [Fact]
    public void Collapse_SeasonReleasedAtOnceStartsAtFirstEpisode()
    {
        var sameDay = _now.AddDays(-3);
        var episodes = Enumerable.Range(1, 8).Reverse().Select(i => Episode($"Office S10E0{i}", _office, 10, i, sameDay)).ToArray();

        Assert.Equal(["Office S10E01"], Names(NewReleasesCollapser.Collapse(episodes)));
    }

    [Fact]
    public void Collapse_OrderedByNewestRelease()
    {
        var svuNew = Episode("SVU S26E06", _svu, 26, 6, _now.AddDays(-1));
        var movie = MovieItem("Movie", _now.AddDays(-4));
        var svuOlder = Episode("SVU S26E05", _svu, 26, 5, _now.AddDays(-8));
        var officeOld = Episode("Office S10E01", _office, 10, 1, _now.AddDays(-20));

        Assert.Equal(
            ["SVU S26E05", "Movie", "Office S10E01"],
            Names(NewReleasesCollapser.Collapse([svuNew, movie, svuOlder, officeOld])));
    }

    [Fact]
    public async Task Query_WindowUnwatchedAvailable()
    {
        var provider = CreateProvider();

        await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 10), CancellationToken.None);

        var q = _query!;
        Assert.Equal(_now.AddDays(-30), q.MinPremiereDate);
        Assert.Equal(_now, q.MaxPremiereDate);
        Assert.False(q.IsPlayed);
        Assert.False(q.IsMissing);
        Assert.True(q.Recursive);
        Assert.Equal([BaseItemKind.Movie, BaseItemKind.Episode], q.IncludeItemTypes);
        Assert.Equal([(ItemSortBy.PremiereDate, SortOrder.Descending)], q.OrderBy);
        Assert.Equal(200, q.Limit);
        Assert.Same(_context.User, q.User);
    }

    [Fact]
    public async Task Query_UsesConfiguredDays()
    {
        var provider = CreateProvider();

        await provider.GetItemsAsync(_context, new JsonObject { ["days"] = 7 }, new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(_now.AddDays(-7), _query!.MinPremiereDate);
    }

    [Fact]
    public async Task Query_SingleMediaType()
    {
        var provider = CreateProvider();

        await provider.GetItemsAsync(_context, new JsonObject { ["mediaTypes"] = new JsonArray("Episode") }, new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal([BaseItemKind.Episode], _query!.IncludeItemTypes);
    }

    [Fact]
    public async Task GetItems_CollapsesAndPages()
    {
        var items = new BaseItem[]
        {
            Episode("SVU S26E06", _svu, 26, 6, _now.AddDays(-1)),
            MovieItem("Movie", _now.AddDays(-4)),
            Episode("SVU S26E05", _svu, 26, 5, _now.AddDays(-8)),
            Episode("Office S10E01", _office, 10, 1, _now.AddDays(-20))
        };
        var provider = CreateProvider(items);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(1, 1), CancellationToken.None);

        Assert.Equal(["Movie"], Names(_adapted!));
        Assert.Equal(3, result.TotalRecordCount);
        Assert.Equal("New Releases", result.Title);
        Assert.Null(result.Error);
        Assert.Equal(HomeRowCardShape.Thumb, result.Presentation.CardShape);
    }

    [Fact]
    public async Task GetItems_EmptyMediaTypes_NeedsConfiguration()
    {
        var provider = CreateProvider();

        var result = await provider.GetItemsAsync(_context, new JsonObject { ["mediaTypes"] = new JsonArray() }, new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal("needs-configuration", result.Error);
        Assert.Empty(result.Items);
        Assert.Null(result.TotalRecordCount);
        Assert.Null(_query);
    }

    [Fact]
    public async Task Schema_Defaults()
    {
        var provider = CreateProvider();

        var schema = await provider.GetSchemaAsync(_context, CancellationToken.None);

        Assert.Equal("new-releases", provider.Id);
        Assert.Equal("New Releases", provider.Name);
        Assert.Equal(["days", "mediaTypes", "cardStyle"], schema.Settings.Select(s => s.Key).ToArray());
        var days = schema.Settings[0];
        Assert.Equal((1, 365, 30), (days.Minimum, days.Maximum, days.Default!.GetValue<int>()));
        var types = schema.Settings[1];
        Assert.Equal(1, types.MinItems);
        Assert.Equal(["Movie", "Episode"], types.Options.Select(o => o.Value).ToArray());
        Assert.Equal("""["Movie","Episode"]""", types.Default!.ToJsonString());
        Assert.Equal("thumb", schema.Settings[2].Default!.GetValue<string>());
    }
}
