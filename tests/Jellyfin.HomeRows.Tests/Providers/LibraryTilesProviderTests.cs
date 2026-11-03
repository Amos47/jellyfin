using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Providers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class LibraryTilesProviderTests
{
    private readonly Mock<IUserViewManager> _views = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(false), DateTime.UtcNow);

    [Theory]
    [InlineData(HomeRowProviderIds.LibraryTiles, "My Media", HomeRowCardShape.Thumb)]
    [InlineData(HomeRowProviderIds.LibraryButtons, "My Media (small)", HomeRowCardShape.Button)]
    public async Task Tiles_SkipsVisibilityCheck(string id, string name, HomeRowCardShape shape)
    {
        var folder = new CollectionFolder { Id = Guid.NewGuid() };
        _views.Setup(v => v.GetUserViews(It.Is<UserViewQuery>(q => q.User == _context.User))).Returns([folder]);
        HomeRowContext? seen = null;
        var dtos = new[] { new BaseItemDto() };
        _adapter
            .Setup(a => a.ToDtos(It.Is<IReadOnlyList<BaseItem>>(i => i.Single() == folder), It.IsAny<HomeRowContext>(), true))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((_, c, _) => seen = c)
            .Returns(dtos);
        var provider = LibraryTilesProvider.CreateAll(_views.Object, _adapter.Object).Single(p => p.Id == id);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(name, provider.Name);
        Assert.Same(dtos, result.Items);
        Assert.Equal(shape, result.Presentation.CardShape);
        Assert.Equal(ImageType.Primary, result.Presentation.PreferredImageType);
        Assert.Contains(ItemFields.PrimaryImageAspectRatio, seen!.DtoOptions.Fields);
        Assert.Contains(ItemFields.DisplayPreferencesId, seen.DtoOptions.Fields);
        Assert.NotSame(_context.DtoOptions, seen.DtoOptions);
        Assert.DoesNotContain(ItemFields.DisplayPreferencesId, _context.DtoOptions.Fields);
    }
}
