using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the seasons and episodes of a show from the server's own TMDB provider, for the show page.
/// </summary>
public interface ITmdbSeasonSource
{
    /// <summary>
    /// Gets the regular seasons (numbered 1 and up) of a show, with their episodes.
    /// </summary>
    /// <param name="seriesTmdbId">The TMDB id of the show.</param>
    /// <param name="language">The metadata language, or null.</param>
    /// <param name="countryCode">The metadata country code, or null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The seasons, or null when TMDB doesn't know the show.</returns>
    Task<IReadOnlyList<TmdbSeason>?> GetSeasonsAsync(int seriesTmdbId, string? language, string? countryCode, CancellationToken ct);
}
