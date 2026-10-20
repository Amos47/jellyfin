using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Providers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class ResumeProviderTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(), DateTime.UtcNow);

    private InternalItemsQuery? _captured;

    private ResumeProvider[] CreateProviders()
    {
        _libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => _captured = q)
            .Returns(new QueryResult<BaseItem>(0, 3, Array.Empty<BaseItem>()));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), false))
            .Returns(Array.Empty<BaseItemDto>());
        return ResumeProvider.CreateAll(_libraryManager.Object, _adapter.Object).ToArray();
    }

    private ResumeProvider Get(string id) => CreateProviders().Single(p => p.Id == id);

    [Fact]
    public async Task Resume_Video_QueryMatchesUpstream()
    {
        var provider = Get(HomeRowProviderIds.Resume);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(4, 9), CancellationToken.None);

        var q = _captured!;
        Assert.Equal([(ItemSortBy.DatePlayed, SortOrder.Descending)], q.OrderBy);
        Assert.True(q.IsResumable);
        Assert.Equal(4, q.StartIndex);
        Assert.Equal(9, q.Limit);
        Assert.Equal(Guid.Empty, q.ParentId);
        Assert.True(q.Recursive);
        Assert.Equal([MediaType.Video], q.MediaTypes);
        Assert.False(q.IsVirtualItem);
        Assert.False(q.CollapseBoxSetItems);
        Assert.True(q.IncludeOwnedItems);
        Assert.True(q.EnableTotalRecordCount);
        Assert.Empty(q.AncestorIds);
        Assert.Same(_context.User, q.User);
        Assert.Same(_context.DtoOptions, q.DtoOptions);
        Assert.Equal(3, result.TotalRecordCount);
        Assert.Equal("Continue Watching", result.Title);
        Assert.Equal(new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary), result.Presentation);
    }

    [Fact]
    public async Task ResumeAudio_UsesAudioMediaType()
    {
        var provider = Get(HomeRowProviderIds.ResumeAudio);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 5), CancellationToken.None);

        Assert.Equal([MediaType.Audio], _captured!.MediaTypes);
        Assert.Equal(new HomeRowPresentation(HomeRowCardShape.Square, ImageType.Primary), result.Presentation);
    }

    [Fact]
    public async Task ResumeBook_UsesBookMediaTypeAndPoster()
    {
        var provider = Get(HomeRowProviderIds.ResumeBook);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 5), CancellationToken.None);

        Assert.Equal([MediaType.Book], _captured!.MediaTypes);
        Assert.Equal(new HomeRowPresentation(HomeRowCardShape.Poster, ImageType.Primary), result.Presentation);
    }

    [Fact]
    public async Task Resume_ExcludedLibraries_AreRemovedFromAncestors()
    {
        var kept = new Mock<Folder>();
        kept.Object.Id = Guid.NewGuid();
        var excluded = new Mock<Folder>();
        excluded.Object.Id = Guid.NewGuid();
        var root = new Mock<UserRootFolder>();
        root.Setup(r => r.GetChildren(_context.User, true, null)).Returns([kept.Object, excluded.Object]);
        _libraryManager.Setup(l => l.GetUserRootFolder()).Returns(root.Object);
        _context.User.SetPreference(PreferenceKind.LatestItemExcludes, [excluded.Object.Id]);
        var provider = Get(HomeRowProviderIds.Resume);

        await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 5), CancellationToken.None);

        Assert.Equal([kept.Object.Id], _captured!.AncestorIds);
    }

    [Fact]
    public async Task Schema_Defaults()
    {
        var providers = CreateProviders();

        foreach (var (id, name, style) in new[]
        {
            (HomeRowProviderIds.Resume, "Continue Watching (in progress only)", "thumb"),
            (HomeRowProviderIds.ResumeAudio, "Continue Listening", "square"),
            (HomeRowProviderIds.ResumeBook, "Continue Reading", "poster")
        })
        {
            var provider = providers.Single(p => p.Id == id);
            Assert.Equal(name, provider.Name);
            var schema = await provider.GetSchemaAsync(_context, CancellationToken.None);
            var setting = Assert.Single(schema.Settings);
            Assert.Equal("cardStyle", setting.Key);
            Assert.Equal(style, setting.Default!.GetValue<string>());
        }
    }
}
