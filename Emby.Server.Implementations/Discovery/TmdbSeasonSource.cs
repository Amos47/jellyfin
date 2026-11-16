using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Providers.Plugins.Tmdb;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the show page's TMDB seasons, from the server's own TMDB provider (<see cref="TmdbClientManager"/>, which
/// caches each answer for an hour), with the provider's key. Only regular seasons (1 and up) are fetched.
/// </summary>
public sealed class TmdbSeasonSource : ITmdbSeasonSource
{
    private const int MaxParallelSeasons = 4;

    private readonly TmdbClientManager _tmdb;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbSeasonSource"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB client manager.</param>
    public TmdbSeasonSource(TmdbClientManager tmdb)
    {
        _tmdb = tmdb;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbSeason>?> GetSeasonsAsync(int seriesTmdbId, string? language, string? countryCode, CancellationToken ct)
    {
        var show = await _tmdb.GetSeriesAsync(seriesTmdbId, language, null, countryCode, ct).ConfigureAwait(false);
        if (show is null)
        {
            return null;
        }

        var numbers = (show.Seasons ?? [])
            .Select(s => s.SeasonNumber)
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        var seasons = new ConcurrentBag<TmdbSeason>();
        await Parallel.ForEachAsync(
            numbers,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSeasons, CancellationToken = ct },
            async (number, token) =>
            {
                var season = await _tmdb.GetSeasonAsync(seriesTmdbId, number, language, null, countryCode, token).ConfigureAwait(false);
                if (season is null)
                {
                    return;
                }

                var episodes = (season.Episodes ?? [])
                    .Where(e => e.EpisodeNumber is > 0 and <= int.MaxValue)
                    .Select(e => new TmdbEpisode(
                        (int)e.EpisodeNumber,
                        e.Name,
                        e.Overview,
                        e.AirDate is { } date ? DateOnly.FromDateTime(date) : null,
                        e.Runtime is > 0 ? e.Runtime : null,
                        string.IsNullOrEmpty(e.StillPath) ? null : e.StillPath))
                    .ToList();
                seasons.Add(new TmdbSeason(number, season.Name, episodes));
            }).ConfigureAwait(false);

        return seasons.OrderBy(s => s.SeasonNumber).ToList();
    }
}
