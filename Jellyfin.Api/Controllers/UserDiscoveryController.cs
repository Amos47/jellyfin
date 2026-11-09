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
/// Fork: the discovery routes every user may call: a discovery list's page and its actions, and the user's own named
/// lists (Saved). A page list id is a discovery list id, or <c>user:{guid}</c> for one of the caller's own lists; another
/// user's list is a 404. The admin-only <see cref="DiscoveryController"/> keeps the routes the discovery app uses.
/// </summary>
[Route("Discovery")]
[Authorize]
[Tags("Discovery")]
public class UserDiscoveryController : BaseJellyfinApiController
{
    private readonly IDiscoveryStore _store;
    private readonly IUserListStore _userLists;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly IDiscoveryRowPlanner _planner;
    private readonly IEnumerable<IDiscoveryListActionProvider> _actions;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="store">Instance of the <see cref="IDiscoveryStore"/> interface.</param>
    /// <param name="userLists">Instance of the <see cref="IUserListStore"/> interface.</param>
    /// <param name="filmSync">Instance of the <see cref="IDiscoveryFilmSync"/> interface.</param>
    /// <param name="planner">Instance of the <see cref="IDiscoveryRowPlanner"/> interface.</param>
    /// <param name="actions">The registered <see cref="IDiscoveryListActionProvider"/>s.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public UserDiscoveryController(
        IDiscoveryStore store,
        IUserListStore userLists,
        IDiscoveryFilmSync filmSync,
        IDiscoveryRowPlanner planner,
        IEnumerable<IDiscoveryListActionProvider> actions,
        IDtoService dtoService,
        IUserManager userManager)
    {
        _store = store;
        _userLists = userLists;
        _filmSync = filmSync;
        _planner = planner;
        _actions = actions;
        _dtoService = dtoService;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets a discovery list's page for the current user: its title, the header images and the films the user can
    /// play (owned, or with a local trailer), in list order, the same films a Home row of the list shows. A user list's
    /// page (<c>user:{guid}</c>) shows every film the user may see, playable or not, newest first, as its row does. Each
    /// header image is the first film's that has one.
    /// </summary>
    /// <param name="listId">The list id, or <c>user:{guid}</c> for one of the user's lists.</param>
    /// <param name="fields">Optional. Additional fields of the items to return.</param>
    /// <param name="enableImageTypes">Optional. The image types to include.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImages">Optional. Include image information.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Page returned.</response>
    /// <response code="404">There is no list with that id (or it is another user's), or the caller has no user.</response>
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
        var films = _planner.GetRowItems(user, list, !DiscoveryPageLists.IsUserList(listId)).Select(f => f.Item).ToArray();
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
                actions.Add(ItemActionDto.From(action));
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

    /// <summary>
    /// Gets the current user's named lists, the default one (Saved) first. Saved is created on the first read.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Lists returned.</response>
    /// <response code="404">The caller has no user.</response>
    /// <returns>The lists.</returns>
    [HttpGet("UserLists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserListDto[]>> GetUserLists(CancellationToken cancellationToken)
    {
        if (GetUser() is not { } user)
        {
            return NotFound();
        }

        var lists = await _userLists.GetListsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return Ok(lists.Select(ToDto).ToArray());
    }

    /// <summary>
    /// Gets one of the current user's named lists.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">List returned.</response>
    /// <response code="404">The user has no list with that id, or the caller has no user.</response>
    /// <returns>The list.</returns>
    [HttpGet("UserLists/{listId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserListDto>> GetUserList([FromRoute, Required] Guid listId, CancellationToken cancellationToken)
    {
        if (GetUser() is not { } user
            || await _userLists.GetListAsync(user.Id, listId, cancellationToken).ConfigureAwait(false) is not { } list)
        {
            return NotFound();
        }

        return Ok(ToDto(list));
    }

    /// <summary>
    /// Adds a film to one of the current user's lists, by TMDB id. The film must be a discovery film the user may see,
    /// or a film the user owns. A film already in the list keeps its place.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="type">Optional: the media type of the TMDB id, Movie (the default) or Series.</param>
    /// <response code="200">Film added; the updated list returned.</response>
    /// <response code="404">The user has no list with that id, the server doesn't know the film, or the caller has no user.</response>
    /// <returns>The updated list.</returns>
    [HttpPost("UserLists/{listId}/Films/{tmdbId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserListDto>> AddUserListFilm(
        [FromRoute, Required] Guid listId,
        [FromRoute, Required] int tmdbId,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        // Only films are known to the server so far: a show it doesn't know can't be added.
        if (GetUser() is not { } user || tmdbId <= 0 || type != DiscoveryMediaType.Movie)
        {
            return NotFound();
        }

        // A discovery film the user may see, else the user's own film: never a film the server doesn't know. Checked
        // and added under the film sync's lock, so a sync can't delete the film in between. Not the request's token for
        // the add: once added, the film stays even if the client hangs up.
        var added = await _filmSync.RunExclusiveAsync(
            async () =>
            {
                string? imdbId;
                if (_filmSync.FindFilm(tmdbId) is { } film && film.IsVisibleStandalone(user))
                {
                    imdbId = film.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
                }
                else if (_filmSync.FindOwned(user, tmdbId, null) is { } owned)
                {
                    imdbId = owned.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
                }
                else
                {
                    return false;
                }

                return await _userLists.AddFilmAsync(user.Id, listId, tmdbId, string.IsNullOrEmpty(imdbId) ? null : imdbId, CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        if (!added)
        {
            return NotFound();
        }

        return await GetUserList(listId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a film from one of the current user's lists. Removing a film that isn't in it changes nothing.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="type">Optional: the media type of the TMDB id, Movie (the default) or Series.</param>
    /// <response code="200">Film removed; the updated list returned.</response>
    /// <response code="404">The user has no list with that id, or the caller has no user.</response>
    /// <returns>The updated list.</returns>
    [HttpDelete("UserLists/{listId}/Films/{tmdbId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserListDto>> RemoveUserListFilm(
        [FromRoute, Required] Guid listId,
        [FromRoute, Required] int tmdbId,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (GetUser() is not { } user
            || !await _userLists.RemoveFilmAsync(user.Id, listId, tmdbId, type, CancellationToken.None).ConfigureAwait(false))
        {
            return NotFound();
        }

        return await GetUserList(listId, cancellationToken).ConfigureAwait(false);
    }

    private static UserListDto ToDto(UserList list)
        => new(
            list.Id,
            list.Name,
            list.IsDefault,
            list.PageListId,
            list.Films.Select(f => new UserListFilmDto(f.TmdbId, f.ImdbId, f.AddedAt, f.Type)).ToArray());

    private static Guid? FirstWith(IEnumerable<BaseItem> films, ImageType type)
        => films.FirstOrDefault(f => f.HasImage(type))?.Id;

    // The calling user. An API key has no user (empty id), and GetUserById throws on an empty id.
    private User? GetUser()
    {
        var userId = RequestHelpers.GetUserId(User, null);
        return userId.IsEmpty() ? null : _userManager.GetUserById(userId);
    }

    // The calling user and the list: a discovery list, or one of the user's own lists (user:{guid}).
    private async Task<(User User, DiscoveryList List)?> ResolveAsync(string listId, CancellationToken cancellationToken)
    {
        if (GetUser() is not { } user)
        {
            return null;
        }

        var list = await DiscoveryPageLists.GetAsync(_store, _userLists, user, listId, cancellationToken).ConfigureAwait(false);
        return list is null ? null : (user, list);
    }
}
