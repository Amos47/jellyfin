using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// The test host's TMDB season source: no TMDB calls in tests. A show set with <see cref="Set"/> has those seasons;
/// any other show has one season of two episodes that aired long ago, so a virtual discovery show can be got.
/// </summary>
public sealed class FakeTmdbSeasonSource : ITmdbSeasonSource
{
    private readonly ConcurrentDictionary<int, IReadOnlyList<TmdbSeason>?> _shows = new();

    /// <summary>
    /// Sets the seasons TMDB lists for a show; null for a show TMDB doesn't know.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="seasons">The seasons.</param>
    public void Set(int tmdbId, IReadOnlyList<TmdbSeason>? seasons) => _shows[tmdbId] = seasons;

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbSeason>?> GetSeasonsAsync(int seriesTmdbId, string? language, string? countryCode, CancellationToken ct)
        => Task.FromResult(_shows.TryGetValue(seriesTmdbId, out var seasons) ? seasons : Default());

    /// <summary>
    /// Builds a season of aired episodes.
    /// </summary>
    /// <param name="number">The season number.</param>
    /// <param name="episodes">The episode count.</param>
    /// <param name="airDate">The air date of every episode, or null for long ago.</param>
    /// <returns>The season.</returns>
    public static TmdbSeason Season(int number, int episodes, DateOnly? airDate = null)
        => new(
            number,
            "Season " + number,
            Enumerable.Range(1, episodes)
                .Select(e => new TmdbEpisode(e, $"Episode {e}", $"Overview of S{number}E{e}", airDate ?? new DateOnly(2001, 1, 1), 22, $"/s{number}e{e}.jpg"))
                .ToList());

    private static IReadOnlyList<TmdbSeason> Default() => [Season(1, 2)];
}
