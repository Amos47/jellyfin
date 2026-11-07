using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Providers;
using Jellyfin.HomeRows.Schema;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class NextUpProviderTests
{
    private readonly Mock<NextUpSource> _source = new(
        new Mock<IUserDataManager>().Object,
        new Mock<ILibraryManager>().Object,
        new Mock<IServerConfigurationManager>().Object,
        new Mock<IRewatchCandidateFinder>().Object);

    private readonly Mock<IHomeRowItemAdapter> _adapter = new();
    private readonly HomeRowContext _context = new(new User("tester", "Default", "Default"), new DtoOptions(), new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));

    private NextUpQuery? _captured;

    private NextUpProvider CreateProvider()
    {
        _source
            .Setup(s => s.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Callback<NextUpQuery, DtoOptions>((q, _) => _captured = q)
            .Returns(new QueryResult<BaseItem>(0, 7, Array.Empty<BaseItem>()));
        _adapter
            .Setup(a => a.ToDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<HomeRowContext>(), false))
            .Returns(Array.Empty<BaseItemDto>());
        return new NextUpProvider(_source.Object, _adapter.Object);
    }

    [Fact]
    public async Task NextUp_CutoffZero_UsesMinValue()
    {
        var provider = CreateProvider();

        await provider.GetItemsAsync(_context, new JsonObject { ["nextUpCutoffDays"] = 0 }, new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(DateTime.MinValue, _captured!.NextUpDateCutoff);
    }

    [Fact]
    public async Task NextUp_CutoffDays_SubtractsFromNow()
    {
        var provider = CreateProvider();

        await provider.GetItemsAsync(_context, new JsonObject { ["nextUpCutoffDays"] = 30 }, new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(_context.UtcNow.AddDays(-30), _captured!.NextUpDateCutoff);
    }

    [Fact]
    public async Task NextUp_PassesRewatchingAndPaging()
    {
        var provider = CreateProvider();

        var result = await provider.GetItemsAsync(
            _context,
            new JsonObject { ["enableRewatching"] = true },
            new HomeRowPaging(5, 12),
            CancellationToken.None);

        Assert.True(_captured!.EnableRewatching);
        Assert.False(_captured.EnableResumable);
        Assert.True(_captured.EnableTotalRecordCount);
        Assert.Equal(5, _captured.StartIndex);
        Assert.Equal(12, _captured.Limit);
        Assert.Same(_context.User, _captured.User);
        Assert.Equal(7, result.TotalRecordCount);
        Assert.Equal(_context.UtcNow.AddDays(-365), _captured.NextUpDateCutoff);
    }

    [Fact]
    public async Task NextUp_Presentation_DefaultsToThumb()
    {
        var provider = CreateProvider();

        var result = await provider.GetItemsAsync(_context, new JsonObject(), new HomeRowPaging(0, 10), CancellationToken.None);

        Assert.Equal(new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb, ImageType.Primary), result.Presentation);
    }

    [Fact]
    public async Task Schema_Defaults()
    {
        var provider = CreateProvider();

        var schema = await provider.GetSchemaAsync(_context, CancellationToken.None);

        Assert.Equal(HomeRowProviderIds.NextUp, provider.Id);
        Assert.Equal("Next Up", provider.Name);
        var rewatch = schema.Settings.Single(s => s.Key == "enableRewatching");
        Assert.Equal(HomeRowSettingType.Boolean, rewatch.Type);
        Assert.False(rewatch.Default!.GetValue<bool>());
        var cutoff = schema.Settings.Single(s => s.Key == "nextUpCutoffDays");
        Assert.Equal(HomeRowSettingType.Integer, cutoff.Type);
        Assert.Equal(0, cutoff.Minimum);
        Assert.Equal(3650, cutoff.Maximum);
        Assert.Equal(365, cutoff.Default!.GetValue<int>());
        var style = schema.Settings.Single(s => s.Key == "cardStyle");
        Assert.Equal("thumb", style.Default!.GetValue<string>());
    }
}
