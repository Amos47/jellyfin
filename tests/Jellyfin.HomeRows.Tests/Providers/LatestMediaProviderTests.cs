using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Providers;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class LatestMediaProviderTests
{
    private readonly Mock<IUserViewManager> _views = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(), new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));
    private readonly CollectionFolder _movies = new() { Id = Guid.NewGuid(), Name = "Movies", CollectionType = CollectionType.movies };

    private LatestItemsQuery? _captured;
    private IReadOnlyList<BaseItem>? _resolved;
    private bool? _skip;

    private LatestMediaProvider CreateProvider(List<Tuple<BaseItem, List<BaseItem>>>? latest = null)
    {
        _views.Setup(v => v.GetUserViews(It.IsAny<UserViewQuery>())).Returns([_movies]);
        _views
            .Setup(v => v.GetLatestItems(It.IsAny<LatestItemsQuery>(), It.IsAny<DtoOptions>()))
            .Callback<LatestItemsQuery, DtoOptions>((q, _) => _captured = q)
            .Returns(latest ?? []);
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((items, _, skip) =>
            {
                _resolved = items;
                _skip = skip;
            })
            .Returns<IReadOnlyList<BaseItem>, HomeRowContext, bool>((items, _, _) => items.Select(_ => new BaseItemDto()).ToArray());
        return new LatestMediaProvider(_views.Object, _adapter.Object);
    }

    private JsonObject Config() => new() { ["library"] = _movies.Id.ToString("N") };

    [Fact]
    public async Task Latest_LibraryOptionsAreUsersLatestViews()
    {
        var provider = CreateProvider();

        var schema = await provider.GetSchemaAsync(_context, CancellationToken.None);

        var library = schema.Settings.Single(s => s.Key == "library");
        Assert.Equal(HomeRowSettingType.Enum, library.Type);
        Assert.Null(library.Default);
        var option = Assert.Single(library.Options);
        Assert.Equal(new HomeRowOption(_movies.Id.ToString("N"), "Movies"), option);
        Assert.Equal("poster", schema.Settings.Single(s => s.Key == "cardStyle").Default!.GetValue<string>());
        Assert.Equal(HomeRowProviderIds.LatestMedia, provider.Id);
        Assert.Equal("Recently Added", provider.Name);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, null)]
    public async Task Latest_HidePlayedInLatest_SetsIsPlayedFalse(bool hide, bool? expected)
    {
        var provider = CreateProvider();
        _context.User.HidePlayedInLatest = hide;

        await provider.GetItemsAsync(_context, Config(), new HomeRowPaging(3, 9), CancellationToken.None);

        Assert.Equal(expected, _captured!.IsPlayed);
        Assert.True(_captured.GroupItems);
        Assert.Equal(9, _captured.Limit);
        Assert.Equal(_movies.Id, _captured.ParentId);
        Assert.Same(_context.User, _captured.User);
    }

    [Fact]
    public async Task Latest_GroupedTupleReturnsParent()
    {
        var parent = new Series { Id = Guid.NewGuid() };
        var single = new Movie { Id = Guid.NewGuid() };
        var album = new MusicAlbum { Id = Guid.NewGuid() };
        var track = new Audio { Id = Guid.NewGuid() };
        var episodes = new List<BaseItem> { new Episode(), new Episode() };
        var provider = CreateProvider(
        [
            Tuple.Create<BaseItem, List<BaseItem>>(parent, episodes),
            Tuple.Create<BaseItem, List<BaseItem>>(null!, [single]),
            Tuple.Create<BaseItem, List<BaseItem>>(album, [track])
        ]);

        var result = await provider.GetItemsAsync(_context, Config(), new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(new BaseItem[] { parent, single, album }, _resolved);
        Assert.True(_skip);
        Assert.Equal(2, result.Items[0].ChildCount);
        Assert.Null(result.Items[1].ChildCount);
        Assert.Equal(1, result.Items[2].ChildCount);
        Assert.Null(result.TotalRecordCount);
        Assert.Equal(new HomeRowPresentation(HomeRowCardShape.Poster, ImageType.Primary), result.Presentation);
    }

    [Fact]
    public async Task Latest_LibraryNoLongerVisible_ReturnsNeedsConfiguration()
    {
        var provider = CreateProvider();

        var result = await provider.GetItemsAsync(
            _context,
            new JsonObject { ["library"] = Guid.NewGuid().ToString("N") },
            new HomeRowPaging(0, 10),
            CancellationToken.None);

        Assert.Equal("needs-configuration", result.Error);
        Assert.Empty(result.Items);
        Assert.Null(result.TotalRecordCount);
        _views.Verify(v => v.GetLatestItems(It.IsAny<LatestItemsQuery>(), It.IsAny<DtoOptions>()), Times.Never);
    }

    [Fact]
    public async Task Latest_TitleUsesLibraryName()
    {
        var provider = CreateProvider();

        var result = await provider.GetItemsAsync(_context, Config(), new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal("Recently Added in Movies", result.Title);
        Assert.Null(result.Error);
    }
}
