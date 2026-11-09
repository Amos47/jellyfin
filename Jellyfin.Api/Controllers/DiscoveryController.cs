using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.DiscoveryDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
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
/// Fork: the discovery hub. Outside apps push lists of films or shows by TMDB id (with a media type).
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
    private readonly IDiscoveryRowPlanner _planner;
    private readonly IDiscoveryUserSettings _settings;
    private readonly IDiscoveryRequestStore _requests;
    private readonly IUserListStore _userLists;
    private readonly IUserManager _userManager;
    private readonly ILogger<DiscoveryController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="store">Instance of the <see cref="IDiscoveryStore"/> interface.</param>
    /// <param name="filmSync">Instance of the <see cref="IDiscoveryFilmSync"/> interface.</param>
    /// <param name="trailers">Instance of the <see cref="IDiscoveryTrailers"/> interface.</param>
    /// <param name="picks">Instance of the <see cref="IDiscoveryUserPicks"/> interface.</param>
    /// <param name="planner">Instance of the <see cref="IDiscoveryRowPlanner"/> interface.</param>
    /// <param name="settings">Instance of the <see cref="IDiscoveryUserSettings"/> interface.</param>
    /// <param name="requests">Instance of the <see cref="IDiscoveryRequestStore"/> interface.</param>
    /// <param name="userLists">Instance of the <see cref="IUserListStore"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DiscoveryController}"/> interface.</param>
    public DiscoveryController(
        IDiscoveryStore store,
        IDiscoveryFilmSync filmSync,
        IDiscoveryTrailers trailers,
        IDiscoveryUserPicks picks,
        IDiscoveryRowPlanner planner,
        IDiscoveryUserSettings settings,
        IDiscoveryRequestStore requests,
        IUserListStore userLists,
        IUserManager userManager,
        ILogger<DiscoveryController> logger)
    {
        _store = store;
        _filmSync = filmSync;
        _trailers = trailers;
        _picks = picks;
        _planner = planner;
        _settings = settings;
        _requests = requests;
        _userLists = userLists;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the titles of one media type the home screens pick on a day: for every user with an enabled discovery row
    /// of the type (Movie discovery for films, the default; Show discovery for <c>?type=Series</c>), the titles of the
    /// lists picked for them. Nothing is recorded as shown.
    /// </summary>
    /// <param name="date">The day (server local), as yyyy-MM-dd. Defaults to tomorrow.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="type">Optional: the media type of the picks, Movie (the default) or Series.</param>
    /// <response code="200">Picks returned.</response>
    /// <returns>The day and the TMDB ids of its picked titles, also with their type.</returns>
    [HttpGet("Picks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryPicksDto>> GetPicks(
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (!Enum.IsDefined(type))
        {
            return BadRequest("The type must be Movie or Series.");
        }

        var day = date ?? DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        var tmdbIds = new List<int>();
        var seen = new HashSet<int>();
        foreach (var user in _userManager.GetUsers())
        {
            IReadOnlyList<DiscoveryList> lists;
            try
            {
                lists = type == DiscoveryMediaType.Movie
                    ? await _picks.GetPickedListsAsync(user, day, cancellationToken).ConfigureAwait(false)
                    : await _picks.GetPickedListsAsync(user, day, type, cancellationToken).ConfigureAwait(false);
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

        return Ok(new DiscoveryPicksDto(day, tmdbIds, type, tmdbIds.Select(id => new DiscoveryPickedItemDto(id, type)).ToArray()));
    }

    /// <summary>
    /// Re-picks a day: stores a new random salt for it and clears every user's saved picks of it, so every user's
    /// Movie discovery rows (and the picks API) pick lists afresh for that day. The new picks hold until the next
    /// re-pick or the next day.
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
        _planner.InvalidateReadiness();
        return Ok(new DiscoveryReshuffleDto(day, salt));
    }

    /// <summary>
    /// Gets the discovery titles of one media type: every title of every list of the type (Movie, the default, or
    /// Series), each once, with whether it has a trailer and when it was shown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="type">Optional: the media type, Movie (the default) or Series.</param>
    /// <response code="200">Films returned.</response>
    /// <returns>The titles.</returns>
    [HttpGet("Films")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryFilmStatusDto[]>> GetFilms(
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (!Enum.IsDefined(type))
        {
            return BadRequest("The type must be Movie or Series.");
        }

        var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);
        var shown = type == DiscoveryMediaType.Movie
            ? await _store.GetShownAsync(cancellationToken).ConfigureAwait(false)
            : await _store.GetShownAsync(type, cancellationToken).ConfigureAwait(false);
        var films = new List<DiscoveryFilmStatusDto>();
        var seen = new HashSet<int>();
        foreach (var tmdbId in lists.Where(l => l.ItemType == type).SelectMany(l => l.Films).Select(f => f.TmdbId).Where(seen.Add))
        {
            // Every listed title has a virtual title once its list is saved; one missing is left out.
            if (_filmSync.FindTitle(tmdbId, type) is not { } film)
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
                log?.LastShown,
                type));
        }

        return Ok(films.ToArray());
    }

    /// <summary>
    /// Gets the discovery lists of every type, each with its <c>ItemType</c>, how many of its films can be played now
    /// and whether that makes it ready.
    /// The films are counted for the calling admin (for an API key, the first administrator): discovery films with a
    /// local trailer, plus films owned in the library. A list is ready with at least the largest <c>minReadyFilms</c>
    /// of the enabled Movie discovery rows in the stored layouts (the admin default and every user's own), or 5 when
    /// there is no such row.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Lists returned.</response>
    /// <returns>The lists.</returns>
    [HttpGet("Lists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryListDto[]>> GetLists(CancellationToken cancellationToken)
    {
        var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);
        var threshold = await _settings.GetReadyThresholdAsync(cancellationToken).ConfigureAwait(false);
        int? seriesThreshold = null;
        var viewer = GetViewer();

        // A film in several lists is checked once per request; a show with the same TMDB id is another title.
        var playable = new Dictionary<(int TmdbId, DiscoveryMediaType Type), bool>();
        int CountReady(DiscoveryList list)
        {
            if (viewer is null)
            {
                return 0;
            }

            var count = 0;
            foreach (var film in list.Films)
            {
                if (!playable.TryGetValue((film.TmdbId, film.Type), out var isPlayable))
                {
                    isPlayable = _planner.IsPlayable(viewer, film);
                    playable[(film.TmdbId, film.Type)] = isPlayable;
                }

                if (isPlayable)
                {
                    count++;
                }
            }

            return count;
        }

        // A show list is ready against the Show discovery rows' bar, a film list against Movie discovery's.
        var dtos = new List<DiscoveryListDto>(lists.Count);
        foreach (var list in lists)
        {
            var bar = threshold;
            if (list.ItemType != DiscoveryMediaType.Movie)
            {
                seriesThreshold ??= await _settings.GetReadyThresholdAsync(DiscoveryMediaType.Series, cancellationToken).ConfigureAwait(false);
                bar = seriesThreshold.Value;
            }

            dtos.Add(ToDto(list, CountReady(list), bar));
        }

        return Ok(dtos.ToArray());
    }

    /// <summary>
    /// Adds a discovery list, or replaces the list with the same id, then creates the virtual films it needs and
    /// removes the ones no list has any more.
    /// </summary>
    /// <param name="listId">The list id: 1 to 64 lowercase letters, digits or dashes.</param>
    /// <param name="list">The list. Its <c>ItemType</c> (default Movie) is the type of all its films.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">List saved.</response>
    /// <response code="400">The id or the list is invalid (including a group over 100 characters or an unknown item type).</response>
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

        var itemType = list.ItemType ?? DiscoveryMediaType.Movie;
        if (!Enum.IsDefined(itemType))
        {
            return BadRequest("The item type must be Movie or Series.");
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
                films.Add(new DiscoveryFilmRef(film.TmdbId, string.IsNullOrWhiteSpace(film.ImdbId) ? null : film.ImdbId.Trim(), itemType));
            }
        }

        await _store.SetListAsync(new DiscoveryList(listId, list.Title, list.Source, films, group, itemType), cancellationToken).ConfigureAwait(false);
        _planner.InvalidateReadiness();

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

        _planner.InvalidateReadiness();

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
    /// <param name="type">Optional: the media type of the TMDB id, Movie (the default) or Series.</param>
    /// <response code="204">Trailer attached.</response>
    /// <response code="400">No trailer folder is configured, or the path is not a file inside it.</response>
    /// <response code="404">There is no discovery title with that TMDB id and type.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Films/{tmdbId}/Trailer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetTrailer(
        [FromRoute, Required] int tmdbId,
        [FromBody, Required] DiscoveryTrailerUpdateDto trailer,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (!Enum.IsDefined(type))
        {
            return BadRequest("The type must be Movie or Series.");
        }

        // A show's trailer goes on the virtual show of that TMDB id, never on a film with the same id.
        var result = type == DiscoveryMediaType.Movie
            ? await _trailers.SetAsync(tmdbId, trailer.Path ?? string.Empty, cancellationToken).ConfigureAwait(false)
            : await _trailers.SetAsync(tmdbId, type, trailer.Path ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (result == DiscoveryTrailerResult.Set)
        {
            _planner.InvalidateReadiness();
        }

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
    /// <param name="type">Optional: the media type of the TMDB id, Movie (the default) or Series.</param>
    /// <response code="204">Trailer removed, or there was none.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Films/{tmdbId}/Trailer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteTrailer(
        [FromRoute, Required] int tmdbId,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (!Enum.IsDefined(type))
        {
            return BadRequest("The type must be Movie or Series.");
        }

        if (type == DiscoveryMediaType.Movie)
        {
            await _trailers.RemoveAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _trailers.RemoveAsync(tmdbId, type, cancellationToken).ConfigureAwait(false);
        }

        _planner.InvalidateReadiness();
        return NoContent();
    }

    /// <summary>
    /// Gets the download requests of one media type (Movie unless <paramref name="type"/> says otherwise), each with
    /// its <c>Type</c>, most recently changed first: an app that knows no types only ever sees films. Requests whose
    /// title is now in a library are marked done first.
    /// </summary>
    /// <param name="state">Optional: only the requests in this state (requested, downloading, failed or done).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="type">Optional: the media type of the requests, Movie (the default) or Series.</param>
    /// <response code="200">Requests returned.</response>
    /// <response code="400">The state is unknown.</response>
    /// <returns>The requests.</returns>
    [HttpGet("Requests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DiscoveryRequestDto[]>> GetRequests(
        [FromQuery] string? state,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (!Enum.IsDefined(type))
        {
            return BadRequest("The type must be Movie or Series.");
        }

        if (state is not null && !DiscoveryRequest.IsState(state))
        {
            return BadRequest("The state must be requested, downloading, failed or done.");
        }

        // Every active request is checked, so one that just finished leaves the requested or downloading list.
        var all = await _requests.GetAllAsync(null, cancellationToken).ConfigureAwait(false);
        var current = await DiscoveryRequestDone.CheckAllAsync(_requests, _filmSync, all, cancellationToken).ConfigureAwait(false);
        return Ok(current
            .Where(r => state is null || string.Equals(r.State, state, StringComparison.Ordinal))
            .Where(r => r.Type == type)
            .Select(r => new DiscoveryRequestDto(r.TmdbId, r.State, r.UpdatedAt, r.UserIds, r.Type))
            .ToArray());
    }

    /// <summary>
    /// Reports the progress of a download request: <c>downloading</c> once Radarr has it, or <c>failed</c>, which
    /// offers Download again. A done request stays done.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the film.</param>
    /// <param name="update">The new state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="type">Optional: the media type of the TMDB id, Movie (the default) or Series.</param>
    /// <response code="204">State set.</response>
    /// <response code="400">The state is not downloading or failed.</response>
    /// <response code="404">Nobody requested the film.</response>
    /// <response code="409">The request is done: the film is in a library.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Requests/{tmdbId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> SetRequestState(
        [FromRoute, Required] int tmdbId,
        [FromBody, Required] DiscoveryRequestUpdateDto update,
        CancellationToken cancellationToken,
        [FromQuery] DiscoveryMediaType type = DiscoveryMediaType.Movie)
    {
        if (!Enum.IsDefined(type))
        {
            return BadRequest("The type must be Movie or Series.");
        }

        if (update.State is not (DiscoveryRequest.StateDownloading or DiscoveryRequest.StateFailed))
        {
            return BadRequest("The state must be downloading or failed.");
        }

        var request = await _requests.GetAsync(tmdbId, type, cancellationToken).ConfigureAwait(false);
        request = await DiscoveryRequestDone.CheckAsync(_requests, _filmSync, request, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return NotFound();
        }

        if (!request.IsActive)
        {
            return Conflict("The request is done: the film is in a library.");
        }

        // Not the request's token: once reported, the state stands even if the client hangs up. The store keeps a
        // request that was marked done meanwhile done.
        var updated = await _requests.SetStateAsync(tmdbId, type, update.State, CancellationToken.None).ConfigureAwait(false);
        return updated is { IsActive: false } ? Conflict("The request is done: the film is in a library.") : NoContent();
    }

    /// <summary>
    /// Sets whether downloads are enabled, per media type. The discovery app sets it at startup, each true only when
    /// its download configuration for that type is complete: <c>Enabled</c> for films, <c>SeriesEnabled</c> for shows.
    /// While one is off, discovery titles of that type offer no Download. Both are off until first set; a missing
    /// <c>SeriesEnabled</c> is false, so an app that knows no shows turns show downloads off.
    /// </summary>
    /// <param name="setting">The setting.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Setting saved.</response>
    /// <response code="400">Enabled is missing.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Settings/Downloads")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SetDownloadsSetting([FromBody, Required] DiscoveryDownloadsSettingDto setting, CancellationToken cancellationToken)
    {
        if (setting.Enabled is not { } enabled)
        {
            return BadRequest("Enabled is required.");
        }

        await _requests.SetDownloadsEnabledAsync(enabled, CancellationToken.None).ConfigureAwait(false);
        await _requests.SetDownloadsEnabledAsync(DiscoveryMediaType.Series, setting.SeriesEnabled ?? false, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets whether downloads are enabled, per media type: <c>Enabled</c> for films, <c>SeriesEnabled</c> for shows.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Setting returned.</response>
    /// <returns>The setting.</returns>
    [HttpGet("Settings/Downloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryDownloadsSettingDto>> GetDownloadsSetting(CancellationToken cancellationToken)
    {
        var movies = await _requests.GetDownloadsEnabledAsync(cancellationToken).ConfigureAwait(false);
        var shows = await _requests.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, cancellationToken).ConfigureAwait(false);
        return Ok(new DiscoveryDownloadsSettingDto(movies, shows));
    }

    /// <summary>
    /// Gets the titles the discovery app must never delete: the ones saved in anyone's list, and the ones with a
    /// download request that isn't done (failed ones included). Requests whose title is now in a library are marked
    /// done first. <c>Items</c> has every type; <c>TmdbIds</c> keeps the films only, for apps that know no types.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Titles returned.</response>
    /// <returns>The typed items, and the films' TMDB ids ascending.</returns>
    [HttpGet("Protected")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoveryProtectedDto>> GetProtected(CancellationToken cancellationToken)
    {
        var all = await _requests.GetAllAsync(null, cancellationToken).ConfigureAwait(false);
        var current = await DiscoveryRequestDone.CheckAllAsync(_requests, _filmSync, all.Where(r => r.IsActive), cancellationToken).ConfigureAwait(false);
        var items = new List<DiscoveryProtectedItemDto>();
        foreach (var type in Enum.GetValues<DiscoveryMediaType>())
        {
            var saved = await _userLists.GetListedTmdbIdsAsync(type, cancellationToken).ConfigureAwait(false);
            var requested = current.Where(r => r.IsActive && r.Type == type).Select(r => r.TmdbId);
            items.AddRange(saved.Concat(requested).Distinct().Order().Select(id => new DiscoveryProtectedItemDto(id, type)));
        }

        var movieIds = items.Where(i => i.Type == DiscoveryMediaType.Movie).Select(i => i.TmdbId).ToArray();
        return Ok(new DiscoveryProtectedDto(movieIds, items));
    }

    private static bool IsValidId(string id) => ListIdRegex().IsMatch(id);

    private static DiscoveryListDto ToDto(DiscoveryList list, int readyFilms, int threshold)
        => new(
            list.Id,
            list.Title,
            list.Source,
            list.Films.Select(f => new DiscoveryFilmDto(f.TmdbId, f.ImdbId)).ToArray(),
            list.Group,
            readyFilms,
            readyFilms >= threshold,
            list.ItemType);

    // The calling admin; an API key has no user, so the first administrator stands in.
    private User? GetViewer()
    {
        var userId = User.GetUserId();
        return (userId.IsEmpty() ? null : _userManager.GetUserById(userId))
            ?? _userManager.GetUsers().FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator));
    }

    // \z rather than $, which would also let a trailing newline through.
    [GeneratedRegex(@"^[a-z0-9-]{1,64}\z")]
    private static partial Regex ListIdRegex();

    private BadRequestObjectResult InvalidId() => BadRequest("The list id must be 1 to 64 lowercase letters, digits or dashes.");
}
