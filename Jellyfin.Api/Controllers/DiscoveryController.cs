using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.DiscoveryDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Fork: the movie discovery hub. Outside apps push lists of films by TMDB id.
/// </summary>
[Route("Discovery")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("Discovery")]
public partial class DiscoveryController : BaseJellyfinApiController
{
    private readonly IDiscoveryStore _store;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly IDiscoveryTrailers _trailers;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="store">Instance of the <see cref="IDiscoveryStore"/> interface.</param>
    /// <param name="filmSync">Instance of the <see cref="IDiscoveryFilmSync"/> interface.</param>
    /// <param name="trailers">Instance of the <see cref="IDiscoveryTrailers"/> interface.</param>
    public DiscoveryController(IDiscoveryStore store, IDiscoveryFilmSync filmSync, IDiscoveryTrailers trailers)
    {
        _store = store;
        _filmSync = filmSync;
        _trailers = trailers;
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
    /// <response code="400">The id or the list is invalid.</response>
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

        await _store.SetListAsync(new DiscoveryList(listId, list.Title, list.Source, films), cancellationToken).ConfigureAwait(false);
        await _filmSync.SyncAsync(cancellationToken).ConfigureAwait(false);
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

        await _filmSync.SyncAsync(cancellationToken).ConfigureAwait(false);
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
        => new(list.Id, list.Title, list.Source, list.Films.Select(f => new DiscoveryFilmDto(f.TmdbId, f.ImdbId)).ToArray());

    // \z rather than $, which would also let a trailing newline through.
    [GeneratedRegex(@"^[a-z0-9-]{1,64}\z")]
    private static partial Regex ListIdRegex();

    private BadRequestObjectResult InvalidId() => BadRequest("The list id must be 1 to 64 lowercase letters, digits or dashes.");
}
