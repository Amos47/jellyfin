using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Api.Models.DiscoveryDtos;
using Jellyfin.Api.Models.ItemActionDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Fork: the discovery routes every user may call: a discovery list's page and its actions. The admin-only
/// <see cref="DiscoveryController"/> keeps the routes the discovery app uses.
/// </summary>
[Route("Discovery")]
[Authorize]
[Tags("Discovery")]
public class UserDiscoveryController : BaseJellyfinApiController
{
    private readonly IDiscoveryStore _store;
    private readonly IDiscoveryRowPlanner _planner;
    private readonly IEnumerable<IDiscoveryListActionProvider> _actions;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="store">Instance of the <see cref="IDiscoveryStore"/> interface.</param>
    /// <param name="planner">Instance of the <see cref="IDiscoveryRowPlanner"/> interface.</param>
    /// <param name="actions">The registered <see cref="IDiscoveryListActionProvider"/>s.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public UserDiscoveryController(
        IDiscoveryStore store,
        IDiscoveryRowPlanner planner,
        IEnumerable<IDiscoveryListActionProvider> actions,
        IDtoService dtoService,
        IUserManager userManager)
    {
        _store = store;
        _planner = planner;
        _actions = actions;
        _dtoService = dtoService;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets a discovery list's page for the current user: its title, the header images and the films the user can
    /// play (owned, or with a local trailer), in list order, the same films a Home row of the list shows. Each header
    /// image is the first film's that has one.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="fields">Optional. Additional fields of the items to return.</param>
    /// <param name="enableImageTypes">Optional. The image types to include.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImages">Optional. Include image information.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Page returned.</response>
    /// <response code="404">There is no list with that id, or the caller has no user.</response>
    /// <returns>The page.</returns>
    [HttpGet("Lists/{listId}/Page")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscoveryListPageDto>> GetListPage(
        [FromRoute, Required] string listId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery] bool? enableImages = true,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveAsync(listId, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            return NotFound();
        }

        var (user, list) = resolved;
        var films = _planner.GetRowItems(user, list, true).Select(f => f.Item).ToArray();
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _dtoService.GetBaseItemDtos(films, dtoOptions, user);
        return Ok(new DiscoveryListPageDto(
            list.Id,
            list.Title,
            null,
            FirstWith(films, ImageType.Backdrop),
            FirstWith(films, ImageType.Primary),
            items));
    }

    /// <summary>
    /// Gets the actions of a discovery list's page for the current user, such as Watch trailers.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Actions returned.</response>
    /// <response code="404">There is no list with that id, or the caller has no user.</response>
    /// <returns>The actions.</returns>
    [HttpGet("Lists/{listId}/Actions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemActionDto[]>> GetListActions([FromRoute, Required] string listId, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(listId, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            return NotFound();
        }

        var (user, list) = resolved;
        var actions = new List<ItemActionDto>();
        foreach (var provider in _actions)
        {
            if (await provider.GetListActionAsync(user, list, cancellationToken).ConfigureAwait(false) is { } action)
            {
                actions.Add(new ItemActionDto(action.Id, action.Label, action.Icon, action.Kind));
            }
        }

        return Ok(actions.ToArray());
    }

    /// <summary>
    /// Gets the items a play-queue list action plays, in order. Watch trailers plays the trailers of every film of the
    /// list's page, in list order.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="actionId">The action id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Queue returned.</response>
    /// <response code="404">There is no list with that id, the caller has no user, or the action doesn't apply to the list.</response>
    /// <returns>The queue.</returns>
    [HttpGet("Lists/{listId}/Actions/{actionId}/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemActionQueueDto>> GetListQueue(
        [FromRoute, Required] string listId,
        [FromRoute, Required] string actionId,
        CancellationToken cancellationToken)
    {
        if (await ResolveAsync(listId, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            return NotFound();
        }

        var (user, list) = resolved;
        var provider = _actions.FirstOrDefault(p => string.Equals(p.ListActionId, actionId, StringComparison.Ordinal));
        if (provider is null
            || await provider.GetListActionAsync(user, list, cancellationToken).ConfigureAwait(false) is not { Kind: ItemAction.PlayQueueKind })
        {
            return NotFound();
        }

        var queue = await provider.GetListQueueEntriesAsync(user, list, cancellationToken).ConfigureAwait(false);
        return Ok(ItemActionsController.ToQueueDto(queue));
    }

    private static Guid? FirstWith(IEnumerable<BaseItem> films, ImageType type)
        => films.FirstOrDefault(f => f.HasImage(type))?.Id;

    // The calling user and the list. An API key has no user (empty id), and GetUserById throws on an empty id.
    private async Task<(User User, DiscoveryList List)?> ResolveAsync(string listId, CancellationToken cancellationToken)
    {
        var userId = RequestHelpers.GetUserId(User, null);
        var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var list = await _store.GetListAsync(listId, cancellationToken).ConfigureAwait(false);
        return list is null ? null : (user, list);
    }
}
