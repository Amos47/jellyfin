using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Providers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class GenreProviderTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(), DateTime.UtcNow);
    private readonly List<InternalItemsQuery> _queries = [];
    private readonly List<InternalItemsQuery> _genreQueries = [];

    private GenreProvider CreateProvider(params string[] genres)
    {
        var tuples = genres
            .Select(g => ((BaseItem)new Genre { Name = g }, new ItemCounts()))
            .ToList();
        _libraryManager
            .Setup(l => l.GetGenres(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => _genreQueries.Add(q))
            .Returns(new QueryResult<(BaseItem Item, ItemCounts ItemCounts)>(tuples));
        _libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => _queries.Add(q))
            .Returns(new QueryResult<BaseItem>(5, 42, Array.Empty<BaseItem>()));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), false))
            .Returns(Array.Empty<BaseItemDto>());
        return new GenreProvider(_libraryManager.Object, _adapter.Object);
    }

    private Task<HomeRowResult> Get(GenreProvider provider, JsonObject config, int start = 0, int limit = 100)
        => provider.GetItemsAsync(_context, config, new HomeRowPaging(start, limit), CancellationToken.None);

    [Fact]
    public async Task Schema_GenreOptionsFromLibrary()
    {
        var provider = CreateProvider("Thriller", "Horror", "Comedy");

        var schema = await provider.GetSchemaAsync(_context, CancellationToken.None);

        var genre = schema.Settings.Single(s => s.Key == "genre");
        Assert.Null(genre.Default);
        Assert.Equal(["Comedy", "Horror", "Thriller"], genre.Options.Select(o => o.Value));
        Assert.Equal(["Comedy", "Horror", "Thriller"], genre.Options.Select(o => o.Label));
        Assert.Equal([BaseItemKind.Movie, BaseItemKind.Series], _genreQueries.Single().IncludeItemTypes);
        Assert.True(_genreQueries.Single().Recursive);

        var media = schema.Settings.Single(s => s.Key == "mediaType");
        Assert.Equal("Movie", media.Default!.GetValue<string>());
        Assert.Equal(["Movies", "Shows"], media.Options.Select(o => o.Label));
        Assert.Equal("Random", schema.Settings.Single(s => s.Key == "sort").Default!.GetValue<string>());
        var limit = schema.Settings.Single(s => s.Key == "limit");
        Assert.Equal(20, limit.Default!.GetValue<int>());
        Assert.Equal(1, limit.Minimum);
        Assert.Equal(50, limit.Maximum);
        Assert.Contains(schema.Settings, s => s.Key == "cardStyle");
    }

    [Fact]
    public async Task Query_FiltersGenreAndType()
    {
        var provider = CreateProvider("Horror");

        var result = await Get(provider, new JsonObject { ["genre"] = "Horror", ["mediaType"] = "Series" }, start: 3, limit: 10);

        var q = _queries.Single();
        Assert.Equal(["Horror"], q.Genres);
        Assert.Equal([BaseItemKind.Series], q.IncludeItemTypes);
        Assert.True(q.Recursive);
        Assert.Equal(10, q.Limit);
        Assert.Equal(3, q.StartIndex);
        Assert.Equal(42, result.TotalRecordCount);
        Assert.Null(result.Error);
        Assert.Equal("Horror", result.Title);
    }

    [Fact]
    public async Task Query_LimitIsMinOfConfigAndPaging()
    {
        var provider = CreateProvider("Horror");

        await Get(provider, new JsonObject { ["genre"] = "horror", ["limit"] = 5 }, limit: 100);

        var q = _queries.Single();
        Assert.Equal(5, q.Limit);
        Assert.Equal(["horror"], q.Genres);
        Assert.Equal([BaseItemKind.Movie], q.IncludeItemTypes);
    }

    [Theory]
    [InlineData("Random", ItemSortBy.Random, SortOrder.Descending)]
    [InlineData("DateCreated", ItemSortBy.DateCreated, SortOrder.Descending)]
    [InlineData("PremiereDate", ItemSortBy.PremiereDate, SortOrder.Descending)]
    [InlineData("SortName", ItemSortBy.SortName, SortOrder.Ascending)]
    [InlineData("CommunityRating", ItemSortBy.CommunityRating, SortOrder.Descending)]
    public async Task Query_SortDirection(string sort, ItemSortBy by, SortOrder order)
    {
        var provider = CreateProvider("Horror");

        await Get(provider, new JsonObject { ["genre"] = "Horror", ["sort"] = sort });

        Assert.Equal([(by, order)], _queries.Single().OrderBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedGenre_NeedsConfiguration(bool missingKey)
    {
        var provider = CreateProvider("Comedy");
        var config = missingKey ? new JsonObject() : new JsonObject { ["genre"] = "Horror" };

        var result = await Get(provider, config);

        Assert.Equal("needs-configuration", result.Error);
        Assert.Empty(result.Items);
        Assert.Null(result.TotalRecordCount);
        Assert.Empty(_queries);
    }

    [Fact]
    public async Task TwoInstancesIndependent()
    {
        var provider = CreateProvider("Horror", "Thriller");

        await Get(provider, new JsonObject { ["genre"] = "Horror", ["mediaType"] = "Movie", ["sort"] = "SortName" });
        await Get(provider, new JsonObject { ["genre"] = "Thriller", ["mediaType"] = "Series", ["limit"] = 7 });

        Assert.Equal(2, _queries.Count);
        Assert.Equal(["Horror"], _queries[0].Genres);
        Assert.Equal([BaseItemKind.Movie], _queries[0].IncludeItemTypes);
        Assert.Equal([(ItemSortBy.SortName, SortOrder.Ascending)], _queries[0].OrderBy);
        Assert.Equal(20, _queries[0].Limit);
        Assert.Equal(["Thriller"], _queries[1].Genres);
        Assert.Equal([BaseItemKind.Series], _queries[1].IncludeItemTypes);
        Assert.Equal([(ItemSortBy.Random, SortOrder.Descending)], _queries[1].OrderBy);
        Assert.Equal(7, _queries[1].Limit);
    }
}
