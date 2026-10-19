using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests;

public class HomeRowItemAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToDtos_PassesUserOptionsAndVisibilityFlag(bool skipVisibilityCheck)
    {
        var user = new User("tester", "Default", "Default");
        var options = new DtoOptions();
        var context = new HomeRowContext(user, options, DateTime.UtcNow);
        IReadOnlyList<BaseItem> items = [new Mock<BaseItem>().Object];
        IReadOnlyList<BaseItemDto> expected = [new BaseItemDto()];

        var dtoService = new Mock<IDtoService>();
        dtoService
            .Setup(d => d.GetBaseItemDtos(items, options, user, null, skipVisibilityCheck))
            .Returns(expected);

        var adapter = new HomeRowItemAdapter(dtoService.Object);

        Assert.Same(expected, adapter.ToDtos(items, context, skipVisibilityCheck));
        dtoService.VerifyAll();
    }
}
