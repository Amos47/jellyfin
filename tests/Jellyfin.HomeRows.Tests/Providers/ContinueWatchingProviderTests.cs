using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Providers.ContinueWatching;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Configuration;
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

public class ContinueWatchingProviderTests
{
    private readonly Mock<NextUpSource> _source = new(
        new Mock<IUserDataManager>().Object,
        new Mock<ILibraryManager>().Object,
        new Mock<IServerConfigurationManager>().Object);

    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(), new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));

    private readonly List<InternalItemsQuery> _queries = [];
    private NextUpQuery? _nextUpQuery;
    private IReadOnlyList<BaseItem>? _adapted;

    private ContinueWatchingProvider CreateProvider(BaseItem[] resume, BaseItem[] nextUp, BaseItem[]? recent = null)
    {
        _libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                _queries.Add(q);
                var items = q.IsResumable == true ? resume : recent ?? [];
                return new QueryResult<BaseItem>(0, items.Length, items);
            });
        _source
            .Setup(s => s.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Callback<NextUpQuery, DtoOptions>((q, _) => _nextUpQuery = q)
            .Returns(new QueryResult<BaseItem>(0, nextUp.Length, nextUp));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), false))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((items, _, _) => _adapted = items)
            .Returns(Array.Empty<BaseItemDto>());
        return new ContinueWatchingProvider(_source.Object, _libraryManager.Object, _userData.Object, _adapter.Object);
    }

    [Fact]
    public async Task Provider_ExcludesMoviesWhenDisabled()
    {
        var provider = CreateProvider([], []);

        await provider.GetItemsAsync(_context, new JsonObject { ["includeMovies"] = false }, new HomeRowPaging(0, 10), CancellationToken.None);

        var resume = _queries.Single(q => q.IsResumable == true);
        Assert.Contains(BaseItemKind.AudioBook, resume.ExcludeItemTypes);
        Assert.Contains(BaseItemKind.Movie, resume.ExcludeItemTypes);
        Assert.Equal(50, resume.Limit);
        Assert.Equal(0, resume.StartIndex);
        Assert.Same(_context.DtoOptions, resume.DtoOptions);

        await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 10), CancellationToken.None);

        var withMovies = _queries.Where(q => q.IsResumable == true).Last();
        Assert.Contains(BaseItemKind.AudioBook, withMovies.ExcludeItemTypes);
        Assert.DoesNotContain(BaseItemKind.Movie, withMovies.ExcludeItemTypes);
    }

    [Fact]
    public async Task Provider_InternalQueriesLoadUserDataEvenWhenClientDisabledIt()
    {
        var clientOptions = new DtoOptions { EnableUserData = false };
        var context = new HomeRowContext(_context.User, clientOptions, _context.UtcNow);
        DtoOptions? nextUpOptions = null;
        HomeRowContext? adaptedContext = null;
        var provider = CreateProvider([], []);
        _source
            .Setup(s => s.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Callback<NextUpQuery, DtoOptions>((_, o) => nextUpOptions = o)
            .Returns(new QueryResult<BaseItem>(0, 0, []));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), false))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((_, c, _) => adaptedContext = c)
            .Returns(Array.Empty<BaseItemDto>());

        await provider.GetItemsAsync(context, new JsonObject(), new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(2, _queries.Count);
        Assert.All(_queries, q => Assert.True(q.DtoOptions.EnableUserData));
        Assert.True(nextUpOptions!.EnableUserData);
        Assert.False(clientOptions.EnableUserData);
        Assert.Same(context, adaptedContext);
    }

    [Fact]
    public async Task Provider_NextUpQueryHasResumableFalse()
    {
        var provider = CreateProvider([], []);

        await provider.GetItemsAsync(
            _context,
            new JsonObject { ["enableRewatching"] = true, ["nextUpCutoffDays"] = 30 },
            new HomeRowPaging(0, 10),
            CancellationToken.None);

        Assert.False(_nextUpQuery!.EnableResumable);
        Assert.True(_nextUpQuery.EnableRewatching);
        Assert.Equal(50, _nextUpQuery.Limit);
        Assert.Equal(_context.UtcNow.AddDays(-30), _nextUpQuery.NextUpDateCutoff);
        Assert.Same(_context.User, _nextUpQuery.User);

        var recent = _queries.Single(q => q.IsResumable != true);
        Assert.True(recent.Recursive);
        Assert.Equal([BaseItemKind.Episode], recent.IncludeItemTypes);
        Assert.True(recent.IsPlayed);
        Assert.Equal([(ItemSortBy.DatePlayed, SortOrder.Descending)], recent.OrderBy);
        Assert.Equal(100, recent.Limit);
    }

    [Fact]
    public async Task Provider_PagesCombinedList()
    {
        var movies = Enumerable.Range(0, 5)
            .Select(i => (BaseItem)new Movie { Id = Guid.NewGuid(), Name = "M" + i })
            .ToArray();
        _userData
            .Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns<User, BaseItem>((_, item) => new UserItemData
            {
                Key = string.Empty,
                LastPlayedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-int.Parse(item.Name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture))
            });
        var provider = CreateProvider(movies, []);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(1, 2), CancellationToken.None);

        Assert.Equal(5, result.TotalRecordCount);
        Assert.Equal(["M1", "M2"], _adapted!.Select(i => i.Name).ToArray());
    }
}
