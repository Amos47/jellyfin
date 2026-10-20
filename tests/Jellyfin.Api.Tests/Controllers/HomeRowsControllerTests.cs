using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.HomeRowDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class HomeRowsControllerTests
{
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IHomeRowProviderFactory> _providerFactory = new();
    private readonly Mock<IHomeRowConfigurationService> _configurationService = new();
    private readonly Mock<ILogger<HomeRowsController>> _logger = new();
    private readonly HomeRowsController _subject;
    private readonly User _tokenUser;

    public HomeRowsControllerTests()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var principal = TestHelpers.SetupUser(_userManager, httpContextAccessor, UserRoles.User);
        _tokenUser = _userManager.Object.GetUserById(Guid.Empty)!;
        _subject = new HomeRowsController(_userManager.Object, _providerFactory.Object, _configurationService.Object, _logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
    }

    [Fact]
    public async Task GetRowItems_RowOfAnotherUser_NotFound()
    {
        // The row id exists in another user's layout, but FindRowAsync only searches the caller's layout.
        var rowId = Guid.NewGuid();
        _configurationService
            .Setup(s => s.FindRowAsync(It.IsAny<User>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EffectiveHomeRow?)null);

        var result = await GetRowItems(rowId);

        Assert.IsType<NotFoundResult>(result.Result);
        _configurationService.Verify(
            s => s.FindRowAsync(It.Is<User>(u => u.Id.Equals(_tokenUser.Id)), rowId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetProviders_SchemaThrows_SkipsOnlyThatProvider()
    {
        var failing = SetupProvider("failing", new HomeRowSchema([]));
        failing
            .Setup(p => p.GetSchemaAsync(It.IsAny<HomeRowContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var working = SetupProvider("working", new HomeRowSchema([new HomeRowSetting("count", HomeRowSettingType.Integer, "Count") { Default = 5 }]));
        _providerFactory.SetupGet(f => f.Providers).Returns([failing.Object, working.Object]);

        var result = await _subject.GetProviders(CancellationToken.None);

        var dto = Assert.IsType<HomeRowProvidersDto>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, dto.ApiVersion);
        var provider = Assert.Single(dto.Providers);
        Assert.Equal("working", provider.Id);
        Assert.Equal(5, (int)provider.Defaults["count"]!);
        _logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetProviders_RequestCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = SetupProvider("cancelled", new HomeRowSchema([]));
        provider
            .Setup(p => p.GetSchemaAsync(It.IsAny<HomeRowContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        _providerFactory.SetupGet(f => f.Providers).Returns([provider.Object]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => _subject.GetProviders(cts.Token));
    }

    [Fact]
    public async Task GetRowItems_ProviderThrows_500OnlyForThatRow()
    {
        var failing = SetupProvider("failing", new HomeRowSchema([]));
        failing
            .Setup(p => p.GetItemsAsync(It.IsAny<HomeRowContext>(), It.IsAny<JsonObject>(), It.IsAny<HomeRowPaging>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var working = SetupProvider("working", new HomeRowSchema([]));
        working
            .Setup(p => p.GetItemsAsync(It.IsAny<HomeRowContext>(), It.IsAny<JsonObject>(), It.IsAny<HomeRowPaging>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HomeRowResult([new BaseItemDto()], 1, new HomeRowPresentation(HomeRowCardShape.Thumb, ImageType.Thumb)));
        var failingRow = SetupRow("failing", null, new JsonObject());
        var workingRow = SetupRow("working", null, new JsonObject());

        var failed = await GetRowItems(failingRow);
        var succeeded = await GetRowItems(workingRow);

        var error = Assert.IsType<ObjectResult>(failed.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, error.StatusCode);
        Assert.IsType<ProblemDetails>(error.Value);
        _logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        var ok = Assert.IsAssignableFrom<OkObjectResult>(succeeded.Result);
        var dto = Assert.IsType<HomeRowItemsDto>(ok.Value);
        Assert.Equal(workingRow, dto.RowId);
        Assert.Single(dto.Items);
        Assert.Null(dto.Error);
    }

    [Fact]
    public async Task GetRowItems_RequiredSettingMissing_NeedsConfiguration()
    {
        var schema = new HomeRowSchema([new HomeRowSetting("genre", HomeRowSettingType.String, "Genre")]);
        var provider = SetupProvider("genre", schema);
        var rowId = SetupRow("genre", "My row", new JsonObject());

        var result = await GetRowItems(rowId);

        var ok = Assert.IsAssignableFrom<OkObjectResult>(result.Result);
        var dto = Assert.IsType<HomeRowItemsDto>(ok.Value);
        Assert.Equal("needs-configuration", dto.Error);
        Assert.Empty(dto.Items);
        Assert.Null(dto.TotalRecordCount);
        Assert.Equal("My row", dto.Title);
        Assert.Equal(new HomeRowPresentation(HomeRowCardShape.Poster, ImageType.Primary), dto.Presentation);
        provider.Verify(
            p => p.GetItemsAsync(It.IsAny<HomeRowContext>(), It.IsAny<JsonObject>(), It.IsAny<HomeRowPaging>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRowItems_InstanceTitleOverridesProviderTitle()
    {
        var schema = new HomeRowSchema([new HomeRowSetting("count", HomeRowSettingType.Integer, "Count") { Default = 5 }]);
        var provider = SetupProvider("titled", schema);
        provider
            .Setup(p => p.GetItemsAsync(It.IsAny<HomeRowContext>(), It.IsAny<JsonObject>(), It.IsAny<HomeRowPaging>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HomeRowResult([], 0, new HomeRowPresentation(HomeRowCardShape.Poster, ImageType.Primary)) { Title = "Provider title" });
        var titled = SetupRow("titled", "Instance title", new JsonObject { ["count"] = "invalid" });
        var untitled = SetupRow("titled", null, new JsonObject());

        var titledResult = await GetRowItems(titled, startIndex: 3, limit: 7);
        var untitledResult = await GetRowItems(untitled);

        var titledDto = Assert.IsType<HomeRowItemsDto>(Assert.IsAssignableFrom<OkObjectResult>(titledResult.Result).Value);
        Assert.Equal("Instance title", titledDto.Title);
        Assert.Equal(3, titledDto.StartIndex);
        var untitledDto = Assert.IsType<HomeRowItemsDto>(Assert.IsAssignableFrom<OkObjectResult>(untitledResult.Result).Value);
        Assert.Equal("Provider title", untitledDto.Title);

        // An invalid setting with a default is lenient: the provider gets the default.
        provider.Verify(
            p => p.GetItemsAsync(
                It.IsAny<HomeRowContext>(),
                It.Is<JsonObject>(c => (int)c["count"]! == 5),
                It.Is<HomeRowPaging>(paging => paging.StartIndex == 3 && paging.Limit == 7),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GetRowItems_LimitOutOfRange_BadRequest(int limit)
    {
        var result = await GetRowItems(Guid.NewGuid(), limit: limit);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetRowItems_ProviderUnavailable_NotFound()
    {
        var rowId = Guid.NewGuid();
        _configurationService
            .Setup(s => s.FindRowAsync(It.IsAny<User>(), rowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveHomeRow(new HomeRowInstance(rowId, "removed", null, new JsonObject(), true), false));

        var result = await GetRowItems(rowId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_ValidationErrors_ReturnValidationProblemWithRowKeys()
    {
        _configurationService
            .Setup(s => s.SaveUserLayoutAsync(It.IsAny<User>(), It.IsAny<IReadOnlyList<HomeRowInstance>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new HomeRowSaveError(1, "genre", "A value is required."),
                new HomeRowSaveError(1, "title", "Too long."),
                new HomeRowSaveError(-1, "rows", "Too many rows.")
            ]);
        var update = new HomeRowConfigurationUpdateDto(
        [
            new HomeRowInstanceDto(Guid.NewGuid(), "resume", null, new JsonObject(), true, true),
            new HomeRowInstanceDto(Guid.NewGuid(), "genre", null, new JsonObject(), true, true)
        ]);

        var result = await _subject.UpdateConfiguration(update, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal(["A value is required."], problem.Errors["rows[1].genre"]);
        Assert.Equal(["Too long."], problem.Errors["rows[1].title"]);
        Assert.Equal(["Too many rows."], problem.Errors["rows"]);
        _configurationService.Verify(
            s => s.GetEffectiveLayoutAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private Task<ActionResult<HomeRowItemsDto>> GetRowItems(Guid rowId, int startIndex = 0, int limit = 20)
        => _subject.GetRowItems(rowId, [], [], null, null, startIndex, limit, true, CancellationToken.None);

    private Mock<IHomeRowProvider> SetupProvider(string id, HomeRowSchema schema)
    {
        var provider = new Mock<IHomeRowProvider>();
        provider.SetupGet(p => p.Id).Returns(id);
        provider.SetupGet(p => p.Name).Returns(id);
        provider.SetupGet(p => p.Description).Returns(id);
        provider
            .Setup(p => p.GetSchemaAsync(It.IsAny<HomeRowContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(schema);
        _providerFactory.Setup(f => f.Get(id)).Returns(provider.Object);
        return provider;
    }

    private Guid SetupRow(string providerId, string? title, JsonObject config)
    {
        var rowId = Guid.NewGuid();
        _configurationService
            .Setup(s => s.FindRowAsync(It.IsAny<User>(), rowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveHomeRow(new HomeRowInstance(rowId, providerId, title, config, true), true));
        return rowId;
    }
}
