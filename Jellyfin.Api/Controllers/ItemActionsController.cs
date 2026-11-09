using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.ItemActionDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Fork: the actions apps offer on item pages, such as Watch trailer and Save. A trailer's actions are its film's
/// (the user's owned film when they own it, else the discovery copy), without the play-queue ones such as Watch trailer,
/// so a player only needs the id of the item that's playing.
/// </summary>
[Route("Items/{itemId}/Actions")]
[Authorize]
[Tags("ItemActions")]
public class ItemActionsController : BaseJellyfinApiController
{
    private readonly IEnumerable<IItemActionProvider> _providers;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDiscoveryFilmSync _filmSync;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemActionsController"/> class.
    /// </summary>
    /// <param name="providers">The registered <see cref="IItemActionProvider"/>s.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="filmSync">Instance of the <see cref="IDiscoveryFilmSync"/> interface, to resolve a trailer to the user's owned film.</param>
    public ItemActionsController(IEnumerable<IItemActionProvider> providers, ILibraryManager libraryManager, IUserManager userManager, IDiscoveryFilmSync filmSync)
    {
        _providers = providers;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _filmSync = filmSync;
    }

    /// <summary>
    /// Gets the actions that apply to an item for the current user. For a trailer, its film's actions, without the
    /// play-queue ones.
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
        var (target, isTrailer) = GetTarget(user, item);

        var actions = new List<ItemActionDto>();
        foreach (var provider in _providers)
        {
            if (await provider.GetActionAsync(user, target, cancellationToken).ConfigureAwait(false) is { } action
                && !(isTrailer && action.Kind == ItemAction.PlayQueueKind))
            {
                actions.Add(ItemActionDto.From(action));
            }
        }

        return Ok(actions.ToArray());
    }

    /// <summary>
    /// Performs an action on an item for the current user, such as flipping Save, and returns the action as it is now.
    /// On a trailer it applies to the trailer's film.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="actionId">The action id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Action performed; the updated action returned.</response>
    /// <response code="400">The action can't be performed (it is only played, such as Watch trailer).</response>
    /// <response code="404">The item doesn't exist, the user may not see it, or the action doesn't apply to it.</response>
    /// <returns>The updated action.</returns>
    [HttpPost("{actionId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemActionDto>> PerformAction(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string actionId,
        CancellationToken cancellationToken)
    {
        if (Resolve(itemId) is not { } resolved)
        {
            return NotFound();
        }

        var (user, item) = resolved;
        var (target, isTrailer) = GetTarget(user, item);

        var provider = _providers.FirstOrDefault(p => string.Equals(p.Id, actionId, StringComparison.Ordinal));
        if (provider is null
            || await provider.GetActionAsync(user, target, cancellationToken).ConfigureAwait(false) is not { } current
            || (isTrailer && current.Kind == ItemAction.PlayQueueKind))
        {
            return NotFound();
        }

        // Not the request's token: once performed, the change stands even if the client hangs up.
        var updated = await provider.PerformAsync(user, target, CancellationToken.None).ConfigureAwait(false);
        return updated is null ? BadRequest("The action can't be performed.") : Ok(ItemActionDto.From(updated));
    }

    /// <summary>
    /// Gets the items a play-queue action plays, in order.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="actionId">The action id.</param>
    /// <param name="listId">
    /// Optional: the discovery list the queue is started from (its page, or a film opened from it). When the item is
    /// one of that list's playable films, Watch trailer goes on through that list; otherwise it is ignored.
    /// </param>
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
        [FromQuery] string? listId,
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

        var queue = await provider.GetQueueEntriesAsync(user, item, listId, cancellationToken).ConfigureAwait(false);
        return Ok(ToQueueDto(queue));
    }

    /// <summary>
    /// Builds the queue DTO of a queue's entries.
    /// </summary>
    /// <param name="queue">The entries, in play order.</param>
    /// <returns>The DTO.</returns>
    internal static ItemActionQueueDto ToQueueDto(IReadOnlyList<QueueEntry> queue)
        => new(
            queue.Select(e => e.Id).ToArray(),
            queue.Select(e => new ItemActionQueueItemDto(e.Id, e.Title, e.Subtitle, e.FilmId)).ToArray());

    // What a trailer's actions apply to: its film, the owned one when the trailer belongs to the hidden discovery copy
    // of a film the user owns. The caller has checked that the user may see the trailer. Anything else is itself.
    private (BaseItem Target, bool IsTrailer) GetTarget(User user, BaseItem item)
    {
        if (item.ExtraType != ExtraType.Trailer
            || item.OwnerId.IsEmpty()
            || _libraryManager.GetItemById(item.OwnerId) is not { } film)
        {
            return (item, false);
        }

        if (film is Movie
            && int.TryParse(film.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId)
            && _filmSync.FindFilm(tmdbId) is { } copy
            && copy.Id.Equals(film.Id)
            && _filmSync.FindOwned(user, tmdbId, film.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb)) is { } owned
            && _libraryManager.GetItemById(owned.Id) is { } ownedFilm)
        {
            return (ownedFilm, true);
        }

        return (film, true);
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
