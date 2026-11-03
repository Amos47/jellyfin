using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Providers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class LiveTvProvidersTests
{
    private readonly Mock<ILiveTvManager> _liveTv = new();
    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context;

    public LiveTvProvidersTests()
    {
        var user = new User("tester", "Default", "Default");
        user.SetPermission(PermissionKind.EnableLiveTvAccess, true);
        _context = new HomeRowContext(user, new DtoOptions(), DateTime.UtcNow);
    }

    private static HomeRowContext NoAccessContext()
    {
        var user = new User("tester", "Default", "Default");
        user.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        return new HomeRowContext(user, new DtoOptions(), DateTime.UtcNow);
    }

    [Fact]
    public async Task LiveTv_QueriesAiring()
    {
        InternalItemsQuery? captured = null;
        var item = new Mock<BaseItem>().Object;
        var dtos = new[] { new BaseItemDto() };
        _liveTv
            .Setup(l => l.GetRecommendedProgramsInternal(It.IsAny<InternalItemsQuery>(), _context.DtoOptions, CancellationToken.None))
            .Callback<InternalItemsQuery, DtoOptions, CancellationToken>((q, _, _) => captured = q)
            .Returns(new QueryResult<BaseItem>(0, 4, [item]));
        _adapter.Setup(a => a.ToDtos(It.Is<IReadOnlyList<BaseItem>>(i => i[0] == item), _context, false)).Returns(dtos);
        var provider = new LiveTvProvider(_liveTv.Object, _adapter.Object);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 8), CancellationToken.None);

        Assert.Equal(HomeRowProviderIds.LiveTv, provider.Id);
        Assert.Equal("On Now", provider.Name);
        Assert.True(captured!.IsAiring);
        Assert.Equal(8, captured.Limit);
        Assert.Same(_context.User, captured.User);
        Assert.Same(dtos, result.Items);
        Assert.Equal(HomeRowCardShape.Thumb, result.Presentation.CardShape);
    }

    [Fact]
    public async Task Recordings_QueriesInProgress()
    {
        RecordingQuery? captured = null;
        var dtos = new[] { new BaseItemDto() };
        _liveTv
            .Setup(l => l.GetRecordingsAsync(It.IsAny<RecordingQuery>(), _context.DtoOptions))
            .Callback<RecordingQuery, DtoOptions>((q, _) => captured = q)
            .ReturnsAsync(new QueryResult<BaseItemDto>(0, 1, dtos));
        var provider = new ActiveRecordingsProvider(_liveTv.Object);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 6), CancellationToken.None);

        Assert.Equal(HomeRowProviderIds.ActiveRecordings, provider.Id);
        Assert.Equal("Recordings", provider.Name);
        Assert.True(captured!.IsInProgress);
        Assert.Equal(6, captured.Limit);
        Assert.Equal(_context.User.Id, captured.UserId);
        Assert.Same(dtos, result.Items);
        Assert.Equal(1, result.TotalRecordCount);
    }

    [Fact]
    public async Task LiveTv_WithoutLiveTvPermission_ReturnsEmptyWithoutQuerying()
    {
        var provider = new LiveTvProvider(_liveTv.Object, _adapter.Object);

        var result = await provider.GetItemsAsync(NoAccessContext(), new JsonObject(), new HomeRowPaging(0, 8), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
        Assert.Null(result.Error);
        _liveTv.VerifyNoOtherCalls();
        _adapter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Recordings_WithoutLiveTvPermission_ReturnsEmptyWithoutQuerying()
    {
        var provider = new ActiveRecordingsProvider(_liveTv.Object);

        var result = await provider.GetItemsAsync(NoAccessContext(), new JsonObject(), new HomeRowPaging(0, 6), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
        _liveTv.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LiveTv_StartIndex_SkipsItemsAndTotalMatchesFetchedList()
    {
        InternalItemsQuery? captured = null;
        var first = new Mock<BaseItem>().Object;
        var second = new Mock<BaseItem>().Object;
        var third = new Mock<BaseItem>().Object;
        IReadOnlyList<BaseItem>? adapted = null;
        _liveTv
            .Setup(l => l.GetRecommendedProgramsInternal(It.IsAny<InternalItemsQuery>(), _context.DtoOptions, CancellationToken.None))
            .Callback<InternalItemsQuery, DtoOptions, CancellationToken>((q, _, _) => captured = q)
            .Returns(new QueryResult<BaseItem>(0, 200, [first, second, third]));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), _context, false))
            .Callback<IReadOnlyList<BaseItem>, HomeRowContext, bool>((i, _, _) => adapted = i)
            .Returns([new BaseItemDto()]);
        var provider = new LiveTvProvider(_liveTv.Object, _adapter.Object);

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(1, 2), CancellationToken.None);

        Assert.Equal(3, captured!.Limit);
        Assert.Equal([second, third], adapted);
        Assert.Equal(3, result.TotalRecordCount);
    }
}
