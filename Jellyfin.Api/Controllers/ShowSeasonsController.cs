using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.ShowSeasonsDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Fork: the show page with every season, on the server or not, and Get season / Get series, which ask the discovery
/// app to fetch seasons to the server through Sonarr. Nothing here saves to a phone, and nothing is added to the library.
/// </summary>
[Route("Shows")]
[Authorize]
[Tags("ShowSeasons")]
public class ShowSeasonsController : BaseJellyfinApiController
{
    private readonly IShowSeasonsService _showSeasons;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowSeasonsController"/> class.
    /// </summary>
    /// <param name="showSeasons">Instance of the <see cref="IShowSeasonsService"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public ShowSeasonsController(IShowSeasonsService showSeasons, ILibraryManager libraryManager, IUserManager userManager)
    {
        _showSeasons = showSeasons;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets every season and episode of a show with its state: the library's, plus TMDB's for what isn't on the server.
    /// Without a TMDB id, or when TMDB fails, the library's seasons only, with nothing to get.
    /// </summary>
    /// <param name="seriesId">The show's item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Seasons returned.</response>
    /// <response code="404">The show doesn't exist, isn't a show, or the user may not see it.</response>
    /// <returns>The seasons.</returns>
    [HttpGet("{seriesId}/FullSeasons")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShowSeasonsDto>> GetFullSeasons([FromRoute, Required] Guid seriesId, CancellationToken cancellationToken)
    {
        if (Resolve(seriesId) is not { } resolved)
        {
            return NotFound();
        }

        var page = await _showSeasons.GetPageAsync(resolved.User, resolved.Series, cancellationToken).ConfigureAwait(false);
        return Ok(ToDto(page));
    }

    /// <summary>
    /// Gets one season to the server ("Get season N"). A virtual discovery show is also saved to the user's Saved list.
    /// </summary>
    /// <param name="seriesId">The show's item id.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <response code="204">The season is requested.</response>
    /// <response code="404">The show doesn't exist, the user may not see it, or it has no TMDB id.</response>
    /// <response code="409">The season can't be got: on the server, on its way, unaired, specials, not listed, or show downloads are off.</response>
    /// <returns>No content.</returns>
    [HttpPost("{seriesId}/Seasons/{seasonNumber}/Get")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> GetSeason([FromRoute, Required] Guid seriesId, [FromRoute, Required] int seasonNumber)
        => GetAsync(seriesId, [seasonNumber]);

    /// <summary>
    /// Gets every season that can be got to the server ("Get series"). A virtual discovery show is also saved to the
    /// user's Saved list.
    /// </summary>
    /// <param name="seriesId">The show's item id.</param>
    /// <response code="204">The seasons are requested.</response>
    /// <response code="404">The show doesn't exist, the user may not see it, or it has no TMDB id.</response>
    /// <response code="409">No season can be got.</response>
    /// <returns>No content.</returns>
    [HttpPost("{seriesId}/GetAll")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> GetAll([FromRoute, Required] Guid seriesId)
        => GetAsync(seriesId, null);

    private static ShowSeasonsDto ToDto(ShowSeasonsPage page)
        => new(
            page.SeriesTmdbId,
            page.CanGetSeries,
            page.GetSeriesSeasonCount,
            page.Seasons.Select(s => new ShowSeasonDto(
                s.SeasonNumber,
                s.Name,
                s.State,
                s.OnServer,
                s.Aired,
                s.Total,
                s.CanGet,
                s.Episodes.Select(e => new ShowEpisodeDto(
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.Name,
                    e.Overview,
                    e.AirDate,
                    e.RuntimeMinutes,
                    e.StillUrl,
                    e.ItemId,
                    e.State)).ToArray())).ToArray());

    private async Task<ActionResult> GetAsync(Guid seriesId, int[]? seasonNumbers)
    {
        if (Resolve(seriesId) is not { } resolved)
        {
            return NotFound();
        }

        // Not the request's token: once requested, the request stands even if the client hangs up.
        var result = await _showSeasons.GetSeasonsAsync(resolved.User, resolved.Series, seasonNumbers, requestDiscoverySeries: false, CancellationToken.None).ConfigureAwait(false);
        return result switch
        {
            ShowSeasonsGetResult.Requested => NoContent(),
            ShowSeasonsGetResult.NoTmdbId => NotFound(),
            _ => Conflict("That can't be got now."),
        };
    }

    // The show as the detail endpoint resolves it for the user, so a hidden or restricted item is a 404 here too.
    private (User User, Series Series)? Resolve(Guid seriesId)
    {
        // An API key has no user (empty id), and GetUserById throws on an empty id: answer 404.
        var userId = RequestHelpers.GetUserId(User, null);
        var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        return _libraryManager.GetItemById<BaseItem>(seriesId, user) is Series series ? (user, series) : null;
    }
}
