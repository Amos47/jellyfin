using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.ItemActionDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Fork: the actions apps offer on item pages, such as Watch trailer.
/// </summary>
[Route("Items/{itemId}/Actions")]
[Authorize]
[Tags("ItemActions")]
public class ItemActionsController : BaseJellyfinApiController
{
    private readonly IEnumerable<IItemActionProvider> _providers;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemActionsController"/> class.
    /// </summary>
    /// <param name="providers">The registered <see cref="IItemActionProvider"/>s.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public ItemActionsController(IEnumerable<IItemActionProvider> providers, ILibraryManager libraryManager, IUserManager userManager)
    {
        _providers = providers;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the actions that apply to an item for the current user.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Actions returned.</response>
    /// <response code="404">The item doesn't exist or the user may not see it.</response>
    /// <returns>The actions.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemActionDto[]>> GetActions([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        if (Resolve(itemId) is not { } resolved)
        {
            return NotFound();
        }

        var (user, item) = resolved;

        var actions = new List<ItemActionDto>();
        foreach (var provider in _providers)
        {
            if (await provider.GetActionAsync(user, item, cancellationToken).ConfigureAwait(false) is { } action)
            {
                actions.Add(new ItemActionDto(action.Id, action.Label, action.Icon, action.Kind));
            }
        }

        return Ok(actions.ToArray());
    }

    /// <summary>
    /// Gets the items a play-queue action plays, in order.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="actionId">The action id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Queue returned.</response>
    /// <response code="404">The item doesn't exist, the user may not see it, or the action doesn't apply to it.</response>
    /// <returns>The queue.</returns>
    [HttpGet("{actionId}/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemActionQueueDto>> GetQueue(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string actionId,
        CancellationToken cancellationToken)
    {
        if (Resolve(itemId) is not { } resolved)
        {
            return NotFound();
        }

        var (user, item) = resolved;

        var provider = _providers.FirstOrDefault(p => string.Equals(p.Id, actionId, StringComparison.Ordinal));
        if (provider is null
            || await provider.GetActionAsync(user, item, cancellationToken).ConfigureAwait(false) is not { Kind: ItemAction.PlayQueueKind })
        {
            return NotFound();
        }

        var queue = await provider.GetQueueAsync(user, item, cancellationToken).ConfigureAwait(false);
        return Ok(new ItemActionQueueDto(queue.ToArray()));
    }

    // The item as the detail endpoint resolves it for the user, so a hidden or restricted item is a 404 here too.
    private (User User, BaseItem Item)? Resolve(Guid itemId)
    {
        // An API key has no user (empty id), and GetUserById throws on an empty id: answer 404 as documented.
        var userId = RequestHelpers.GetUserId(User, null);
        var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        return item is null ? null : (user, item);
    }
}
