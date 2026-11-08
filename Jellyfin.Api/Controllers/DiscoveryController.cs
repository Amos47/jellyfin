using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.DiscoveryDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Fork: the movie discovery hub. Outside apps push lists of films by TMDB id.
/// </summary>
[Route("Discovery")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("Discovery")]
public partial class DiscoveryController : BaseJellyfinApiController
{
    private const int MaxGroupLength = 100;

    private static readonly ExtraType[] _trailerTypes = [ExtraType.Trailer];

    private readonly IDiscoveryStore _store;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly IDiscoveryTrailers _trailers;
    private readonly IDiscoveryUserPicks _picks;
    private readonly IUserManager _userManager;
    private readonly ILogger<DiscoveryController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="store">Instance of the <see cref="IDiscoveryStore"/> interface.</param>
    /// <param name="filmSync">Instance of the <see cref="IDiscoveryFilmSync"/> interface.</param>
    /// <param name="trailers">Instance of the <see cref="IDiscoveryTrailers"/> interface.</param>
    /// <param name="picks">Instance of the <see cref="IDiscoveryUserPicks"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DiscoveryController}"/> interface.</param>
    public DiscoveryController(
        IDiscoveryStore store,
        IDiscoveryFilmSync filmSync,
        IDiscoveryTrailers trailers,
        IDiscoveryUserPicks picks,
        IUserManager userManager,
        ILogger<DiscoveryController> logger)
    {
        _store = store;
        _filmSync = filmSync;
        _trailers = trailers;
        _picks = picks;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the films the home screens pick on a day: for every user with an enabled Movie discovery row, the films
    /// of the lists picked for them. Nothing is recorded as shown.
    /// </summary>
    /// <param name="date">The day (server local), as yyyy-MM-dd. Defaults to tomorrow.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Picks returned.</response>
    /// <returns>The day and the TMDB ids of its picked films.</returns>
    [HttpGet("Picks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryPicksDto>> GetPicks([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        var tmdbIds = new List<int>();
        var seen = new HashSet<int>();
        foreach (var user in _userManager.GetUsers())
        {
            IReadOnlyList<DiscoveryList> lists;
            try
            {
                lists = await _picks.GetPickedListsAsync(user, day, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's broken layout must not hide everyone else's picks.
                _logger.LogWarning(ex, "Skipping the discovery picks of user {UserId}: their home layout failed to load", user.Id);
                continue;
            }

            foreach (var list in lists)
            {
                tmdbIds.AddRange(list.Films.Select(f => f.TmdbId).Where(seen.Add));
            }
        }

        return Ok(new DiscoveryPicksDto(day, tmdbIds));
    }

    /// <summary>
    /// Re-picks a day: stores a new random salt for it, so every user's Movie discovery rows (and the picks API) pick
    /// lists afresh for that day. The new picks hold until the next re-pick or the next day.
    /// </summary>
    /// <param name="request">Optional: the day to re-pick. The body may be empty.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Day re-picked.</response>
    /// <returns>The day and its new salt.</returns>
    [HttpPost("Picks/Reshuffle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryReshuffleDto>> ReshufflePicks(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DiscoveryReshuffleRequestDto? request,
        CancellationToken cancellationToken)
    {
        var day = request?.Date ?? DateOnly.FromDateTime(DateTime.Now);
        var salt = RandomNumberGenerator.GetHexString(32, lowercase: true);
        await _store.SetPickSaltAsync(day, salt, cancellationToken).ConfigureAwait(false);
        return Ok(new DiscoveryReshuffleDto(day, salt));
    }

    /// <summary>
    /// Gets the discovery films: every film of every list, each once, with whether it has a trailer and when it was
    /// shown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Films returned.</response>
    /// <returns>The films.</returns>
    [HttpGet("Films")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryFilmStatusDto[]>> GetFilms(CancellationToken cancellationToken)
    {
        var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);
        var shown = await _store.GetShownAsync(cancellationToken).ConfigureAwait(false);
        var films = new List<DiscoveryFilmStatusDto>();
        var seen = new HashSet<int>();
        foreach (var tmdbId in lists.SelectMany(l => l.Films).Select(f => f.TmdbId).Where(seen.Add))
        {
            // Every listed film has a virtual film once its list is saved; one missing is left out.
            if (_filmSync.FindFilm(tmdbId) is not { } film)
            {
                continue;
            }

            var log = shown.GetValueOrDefault(tmdbId);
            films.Add(new DiscoveryFilmStatusDto(
                tmdbId,
                film.Id,
                film.Name,
                film.GetExtras(_trailerTypes).Any(),
                log?.FirstShown,
                log?.LastShown));
        }

        return Ok(films.ToArray());
    }

    /// <summary>
    /// Gets the discovery lists.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Lists returned.</response>
    /// <returns>The lists.</returns>
    [HttpGet("Lists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryListDto[]>> GetLists(CancellationToken cancellationToken)
    {
        var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(lists.Select(ToDto).ToArray());
    }

    /// <summary>
    /// Adds a discovery list, or replaces the list with the same id, then creates the virtual films it needs and
    /// removes the ones no list has any more.
    /// </summary>
    /// <param name="listId">The list id: 1 to 64 lowercase letters, digits or dashes.</param>
    /// <param name="list">The list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">List saved.</response>
    /// <response code="400">The id or the list is invalid (including a group over 100 characters).</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Lists/{listId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SetList(
        [FromRoute, Required] string listId,
        [FromBody, Required] DiscoveryListUpdateDto list,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(listId))
        {
            return InvalidId();
        }

        if (string.IsNullOrWhiteSpace(list.Title))
        {
            return BadRequest("The title is required.");
        }

        if (string.IsNullOrWhiteSpace(list.Source))
        {
            return BadRequest("The source is required.");
        }

        if (list.Films is null)
        {
            return BadRequest("The films are required.");
        }

        var group = string.IsNullOrWhiteSpace(list.Group) ? null : list.Group.Trim();
        if (group?.Length > MaxGroupLength)
        {
            return BadRequest($"The group must be at most {MaxGroupLength} characters.");
        }

        var films = new List<DiscoveryFilmRef>(list.Films.Count);
        var seen = new HashSet<int>();
        foreach (var film in list.Films)
        {
            if (film is null || film.TmdbId <= 0)
            {
                return BadRequest("Every film needs a positive TMDB id.");
            }

            if (seen.Add(film.TmdbId))
            {
                films.Add(new DiscoveryFilmRef(film.TmdbId, string.IsNullOrWhiteSpace(film.ImdbId) ? null : film.ImdbId.Trim()));
            }
        }

        await _store.SetListAsync(new DiscoveryList(listId, list.Title, list.Source, films, group), cancellationToken).ConfigureAwait(false);

        // Not the request's token: once the list is saved, the films follow it even if the client hangs up.
        await _filmSync.SyncAsync(CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes a discovery list, and the virtual films no other list has.
    /// </summary>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">List removed.</response>
    /// <response code="400">The id is invalid.</response>
    /// <response code="404">There is no list with that id.</response>
    /// <returns>A <see cref="NoContentResult"/>, or a <see cref="NotFoundResult"/> if there is no such list.</returns>
    [HttpDelete("Lists/{listId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteList([FromRoute, Required] string listId, CancellationToken cancellationToken)
    {
        if (!IsValidId(listId))
        {
            return InvalidId();
        }

        if (!await _store.RemoveListAsync(listId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        // Not the request's token: once the list is removed, the films follow even if the client hangs up.
        await _filmSync.SyncAsync(CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Makes a file the trailer of a discovery film, replacing the trailer it had. The file must be inside the
    /// configured trailer folder; it is never changed or deleted.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="trailer">The trailer file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Trailer attached.</response>
    /// <response code="400">No trailer folder is configured, or the path is not a file inside it.</response>
    /// <response code="404">There is no discovery film with that TMDB id.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Films/{tmdbId}/Trailer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetTrailer(
        [FromRoute, Required] int tmdbId,
        [FromBody, Required] DiscoveryTrailerUpdateDto trailer,
        CancellationToken cancellationToken)
    {
        var result = await _trailers.SetAsync(tmdbId, trailer.Path ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            DiscoveryTrailerResult.Set => NoContent(),
            DiscoveryTrailerResult.FilmNotFound => NotFound(),
            DiscoveryTrailerResult.NoTrailerRoot => BadRequest("No trailer folder is configured."),
            DiscoveryTrailerResult.PathInUse => BadRequest("The file is already a library item."),
            _ => BadRequest("The path must be an existing file inside the trailer folder."),
        };
    }

    /// <summary>
    /// Removes the trailer of a discovery film. The file stays on disk.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Trailer removed, or there was none.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Films/{tmdbId}/Trailer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteTrailer([FromRoute, Required] int tmdbId, CancellationToken cancellationToken)
    {
        await _trailers.RemoveAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private static bool IsValidId(string id) => ListIdRegex().IsMatch(id);

    private static DiscoveryListDto ToDto(DiscoveryList list)
        => new(list.Id, list.Title, list.Source, list.Films.Select(f => new DiscoveryFilmDto(f.TmdbId, f.ImdbId)).ToArray(), list.Group);

    // \z rather than $, which would also let a trailing newline through.
    [GeneratedRegex(@"^[a-z0-9-]{1,64}\z")]
    private static partial Regex ListIdRegex();

    private BadRequestObjectResult InvalidId() => BadRequest("The list id must be 1 to 64 lowercase letters, digits or dashes.");
}
