using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: stores the season requests of shows, and the Sonarr season statuses the discovery app reports, in the fork
/// database.
/// </summary>
public interface ISeasonStore
{
    /// <summary>
    /// Records that a user asked for seasons of a series: each season without a request gets a pending one, a failed or
    /// handled one goes back to pending, and a pending one is left alone. The first requester of a season is kept.
    /// </summary>
    /// <param name="seriesTmdbId">The TMDB id of the series.</param>
    /// <param name="seasons">The season numbers.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RequestAsync(int seriesTmdbId, IReadOnlyCollection<int> seasons, Guid userId, CancellationToken ct);

    /// <summary>
    /// Gets the season requests of a series, by season number.
    /// </summary>
    /// <param name="seriesTmdbId">The TMDB id of the series.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The requests.</returns>
    Task<IReadOnlyList<SeasonRequest>> GetAsync(int seriesTmdbId, CancellationToken ct);

    /// <summary>
    /// Gets every pending season request, by series and then season number.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The requests.</returns>
    Task<IReadOnlyList<SeasonRequest>> GetPendingAsync(CancellationToken ct);

    /// <summary>
    /// Sets the state of a season request, and its reason (kept only for a failed one). Nothing happens when there is no
    /// such request.
    /// </summary>
    /// <param name="seriesTmdbId">The TMDB id of the series.</param>
    /// <param name="season">The season number.</param>
    /// <param name="state">The new state: one of <see cref="SeasonRequest"/>'s states.</param>
    /// <param name="reason">Why it failed, if it did.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="ArgumentException">The state is unknown.</exception>
    Task SetStateAsync(int seriesTmdbId, int season, string state, string? reason, CancellationToken ct);

    /// <summary>
    /// Replaces every Sonarr season status with these, in one transaction: a season left out has no status any more.
    /// </summary>
    /// <param name="all">The statuses. A season listed twice keeps its last status.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="ArgumentException">A status is unknown.</exception>
    Task ReplaceSonarrStatusAsync(IReadOnlyList<SonarrSeasonStatus> all, CancellationToken ct);

    /// <summary>
    /// Gets the Sonarr season statuses of a series, by season number.
    /// </summary>
    /// <param name="seriesTmdbId">The TMDB id of the series.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The statuses.</returns>
    Task<IReadOnlyList<SonarrSeasonStatus>> GetSonarrStatusAsync(int seriesTmdbId, CancellationToken ct);
}
