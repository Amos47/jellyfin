using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Api.Models.HomeRowDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Server driven home rows.
/// </summary>
[Route("HomeRows")]
[Authorize]
[Tags("HomeRows")]
public class HomeRowsController : BaseJellyfinApiController
{
    /// <summary>
    /// The version of the home rows API.
    /// </summary>
    public const int ApiVersion = 1;

    /// <summary>
    /// The error of a row whose required settings are missing or invalid.
    /// </summary>
    public const string NeedsConfigurationError = "needs-configuration";

    private const int MaxLimit = 100;

    private static readonly HomeRowPresentation _fallbackPresentation = new(HomeRowCardShape.Poster, ImageType.Primary);

    private readonly IUserManager _userManager;
    private readonly IHomeRowProviderFactory _providerFactory;
    private readonly IHomeRowConfigurationService _configurationService;
    private readonly ILogger<HomeRowsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeRowsController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="providerFactory">Instance of the <see cref="IHomeRowProviderFactory"/> interface.</param>
    /// <param name="configurationService">Instance of the <see cref="IHomeRowConfigurationService"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{HomeRowsController}"/> interface.</param>
    public HomeRowsController(
        IUserManager userManager,
        IHomeRowProviderFactory providerFactory,
        IHomeRowConfigurationService configurationService,
        ILogger<HomeRowsController> logger)
    {
        _userManager = userManager;
        _providerFactory = providerFactory;
        _configurationService = configurationService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the home row providers, with their settings schema for the current user. A provider whose schema fails is
    /// logged and left out.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Providers returned.</response>
    /// <returns>The providers.</returns>
    [HttpGet("Providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HomeRowProvidersDto>> GetProviders(CancellationToken cancellationToken)
    {
        var user = GetUser();
        if (user is null)
        {
            return NotFound();
        }

        var context = new HomeRowContext(user, new DtoOptions(), DateTime.UtcNow);
        var providers = new List<HomeRowProviderDto>(_providerFactory.Providers.Count);
        foreach (var provider in _providerFactory.Providers)
        {
            HomeRowSchema schema;
            try
            {
                schema = await provider.GetSchemaAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One failing provider must not hide the others.
                _logger.LogError(ex, "Getting the schema of home row provider {ProviderId} failed", provider.Id);
                continue;
            }

            providers.Add(new HomeRowProviderDto(
                provider.Id,
                provider.Name,
                provider.Description,
                HomeRowSchemaWriter.ToJsonSchema(schema),
                HomeRowSchemaWriter.GetDefaults(schema)));
        }

        return Ok(new HomeRowProvidersDto(ApiVersion, providers));
    }

    /// <summary>
    /// Gets the home row layout of the current user, their own or inherited.
    /// </summary>
    /// <param name="expand">Optional. Whether to return the display view, in which each row of an expanding provider
    /// (Movie discovery) becomes the rows it shows today, with derived ids. Without it the layout is returned as
    /// saved, for editing; the display view can't be saved back.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Layout returned.</response>
    /// <returns>The layout.</returns>
    [HttpGet("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HomeRowConfigurationDto>> GetConfiguration(
        [FromQuery] bool expand = false,
        CancellationToken cancellationToken = default)
    {
        var user = GetUser();
        if (user is null)
        {
            return NotFound();
        }

        return Ok(await GetConfigurationDtoAsync(user, expand, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Validates and saves the home row layout of the current user.
    /// </summary>
    /// <param name="update">The layout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Layout saved.</response>
    /// <response code="400">The layout is invalid.</response>
    /// <returns>The saved layout.</returns>
    [HttpPut("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HomeRowConfigurationDto>> UpdateConfiguration(
        [FromBody, Required] HomeRowConfigurationUpdateDto update,
        CancellationToken cancellationToken)
    {
        var user = GetUser();
        if (user is null)
        {
            return NotFound();
        }

        if (update.Rows is null)
        {
            return RowsRequired();
        }

        var errors = await _configurationService.SaveUserLayoutAsync(user, ToInstances(update.Rows), cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return ToValidationProblem(errors);
        }

        return Ok(await GetConfigurationDtoAsync(user, false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Deletes the home row layout of the current user, so that they inherit the default again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Layout reset.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Configuration")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ResetConfiguration(CancellationToken cancellationToken)
    {
        var user = GetUser();
        if (user is null)
        {
            return NotFound();
        }

        await _configurationService.ResetUserLayoutAsync(user, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the items of a row of the current user's layout.
    /// </summary>
    /// <param name="rowId">The row id.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="startIndex">The index of the first item.</param>
    /// <param name="limit">The maximum number of items, from 1 to 100.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Row items returned.</response>
    /// <response code="400">The limit is out of range.</response>
    /// <response code="404">The row is not in the user's layout, or its provider is not available.</response>
    /// <response code="500">The row's provider failed.</response>
    /// <returns>The row items.</returns>
    [HttpGet("{rowId}/Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<HomeRowItemsDto>> GetRowItems(
        [FromRoute, Required] Guid rowId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 20,
        [FromQuery] bool? enableImages = true,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxLimit)
        {
            return BadRequest(string.Create(CultureInfo.InvariantCulture, $"The limit must be between 1 and {MaxLimit}."));
        }

        if (startIndex < 0)
        {
            return BadRequest("The start index must not be negative.");
        }

        var user = GetUser();
        if (user is null)
        {
            return NotFound();
        }

        var effective = await _configurationService.FindRowAsync(user, rowId, cancellationToken).ConfigureAwait(false);
        var provider = effective is { IsAvailable: true } ? _providerFactory.Get(effective.Row.Provider) : null;
        if (effective is null || provider is null)
        {
            return NotFound();
        }

        var row = effective.Row;
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var context = new HomeRowContext(user, dtoOptions, DateTime.UtcNow) { SubRowKey = effective.SubRowKey };
        try
        {
            var schema = await provider.GetSchemaAsync(context, cancellationToken).ConfigureAwait(false);
            var validation = HomeRowConfigValidator.Validate(schema, row.Config);
            var required = schema.Settings.Where(s => s.Default is null).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            if (validation.Errors.Any(e => required.Contains(e.Key)))
            {
                return Ok(new HomeRowItemsDto(rowId, row.Title, [], null, startIndex, _fallbackPresentation, NeedsConfigurationError));
            }

            var result = await provider
                .GetItemsAsync(context, validation.Config, new HomeRowPaging(startIndex, limit), cancellationToken)
                .ConfigureAwait(false);
            // Each row of an expanding provider has a title of its own (Movie discovery: the list's), which a custom
            // title would repeat on every row; there the custom title is only the fallback.
            var title = provider is IExpandingHomeRowProvider ? result.Title ?? row.Title : row.Title ?? result.Title;

            // The requested id: a derived id stays the id of the row the client asked for.
            return Ok(new HomeRowItemsDto(
                rowId,
                title,
                result.Items,
                result.TotalRecordCount,
                startIndex,
                result.Presentation,
                result.Error));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Home row {RowId} of provider {ProviderId} failed", row.Id, row.Provider);
            return Problem(detail: "The home row provider failed.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Gets the server wide default home row layout.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Default layout returned.</response>
    /// <response code="204">No default layout is saved.</response>
    /// <returns>The rows of the default layout.</returns>
    [HttpGet("DefaultConfiguration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<IReadOnlyList<HomeRowInstanceDto>>> GetDefaultConfiguration(CancellationToken cancellationToken)
    {
        var rows = await _configurationService.GetAdminDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return NoContent();
        }

        return Ok(ToDtos(rows));
    }

    /// <summary>
    /// Validates and saves the server wide default home row layout.
    /// </summary>
    /// <param name="update">The layout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Default layout saved.</response>
    /// <response code="400">The layout is invalid.</response>
    /// <returns>The rows of the saved default layout.</returns>
    [HttpPut("DefaultConfiguration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<HomeRowInstanceDto>>> UpdateDefaultConfiguration(
        [FromBody, Required] HomeRowConfigurationUpdateDto update,
        CancellationToken cancellationToken)
    {
        var user = GetUser();
        if (user is null)
        {
            return NotFound();
        }

        if (update.Rows is null)
        {
            return RowsRequired();
        }

        var errors = await _configurationService.SaveAdminDefaultAsync(user, ToInstances(update.Rows), cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return ToValidationProblem(errors);
        }

        var rows = await _configurationService.GetAdminDefaultAsync(cancellationToken).ConfigureAwait(false);
        return Ok(ToDtos(rows ?? []));
    }

    /// <summary>
    /// Deletes the server wide default home row layout. Users without a layout of their own then fall back to their
    /// upstream home sections, else the standard order.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Default layout deleted.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("DefaultConfiguration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResetDefaultConfiguration(CancellationToken cancellationToken)
    {
        await _configurationService.ResetAdminDefaultAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private static HomeRowInstance[] ToInstances(IReadOnlyList<HomeRowInstanceDto> rows)
        => rows.Select(r => new HomeRowInstance(r.Id, r.Provider ?? string.Empty, r.Title, r.Config ?? new JsonObject(), r.Enabled)).ToArray();

    private static HomeRowInstanceDto ToDto(HomeRowInstance row, bool isAvailable)
        => new(row.Id, row.Provider, row.Title, row.Config, row.Enabled, isAvailable);

    private User? GetUser() => _userManager.GetUserById(User.GetUserId());

    private IReadOnlyList<HomeRowInstanceDto> ToDtos(IReadOnlyList<HomeRowInstance> rows)
        => rows.Select(r => ToDto(r, _providerFactory.Get(r.Provider) is not null)).ToArray();

    private async Task<HomeRowConfigurationDto> GetConfigurationDtoAsync(User user, bool expand, CancellationToken cancellationToken)
    {
        var layout = await _configurationService.GetEffectiveLayoutAsync(user, cancellationToken).ConfigureAwait(false);
        if (expand)
        {
            layout = await _configurationService.ExpandAsync(user, layout, cancellationToken).ConfigureAwait(false);
        }

        return new HomeRowConfigurationDto(
            ApiVersion,
            layout.Source,
            layout.Source == HomeRowLayoutSource.User,
            layout.Rows.Select(r => ToDto(r.Row, r.IsAvailable)).ToArray());
    }

    private BadRequestObjectResult RowsRequired()
        => ToValidationProblem([new HomeRowSaveError(-1, "rows", "The rows are required.")]);

    private BadRequestObjectResult ToValidationProblem(IEnumerable<HomeRowSaveError> errors)
    {
        var problem = new ValidationProblemDetails(
            errors
                .GroupBy(e => e.RowIndex < 0 ? e.Key : string.Create(CultureInfo.InvariantCulture, $"rows[{e.RowIndex}].{e.Key}"), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal))
        {
            Status = StatusCodes.Status400BadRequest
        };
        return BadRequest(problem);
    }
}
