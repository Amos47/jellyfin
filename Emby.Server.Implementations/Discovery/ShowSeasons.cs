using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Discovery;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the show page's season and episode states, from the library's episodes, TMDB's seasons, the season requests
/// and Sonarr's season statuses. Pure: everything it needs is passed in.
/// </summary>
public static class ShowSeasons
{
    /// <summary>
    /// The TMDB image URL a still path is appended to.
    /// </summary>
    public const string StillUrlPrefix = "https://image.tmdb.org/t/p/w300";

    /// <summary>
    /// Builds every season of a show, by season number. A season is listed when the library has an episode in it, or
    /// TMDB lists episodes in it; specials (season 0) only when the library has them. Per season, with F the library
    /// episodes in it and A TMDB's episodes aired on or before <paramref name="today"/>:
    /// <list type="number">
    /// <item>F ≥ A with A &gt; 0, or a season only the library has: on the server.</item>
    /// <item>Else a pending request: requested.</item>
    /// <item>Else by Sonarr: downloading, wanted (requested), or idle (partly with F &gt; 0, else not on the server). With nothing aired, only downloading counts.</item>
    /// <item>Else F &gt; 0: partly.</item>
    /// <item>Else not on the server (a failed request too).</item>
    /// </list>
    /// A season can be got when <paramref name="canGet"/>, A &gt; F, Sonarr's numbering matches, it isn't specials
    /// (nor a negative number), and it isn't requested or downloading.
    /// </summary>
    /// <param name="library">The library's episodes of the show.</param>
    /// <param name="tmdb">TMDB's seasons of the show, or null when there are none (no TMDB id, or TMDB failed).</param>
    /// <param name="requests">The show's season requests.</param>
    /// <param name="sonarr">The show's Sonarr season statuses.</param>
    /// <param name="today">Today, for the air dates.</param>
    /// <param name="canGet">Whether seasons can be got at all (the app has show downloads enabled).</param>
    /// <returns>The seasons.</returns>
    public static IReadOnlyList<SeasonView> Build(
        IReadOnlyList<LibraryEpisode> library,
        IReadOnlyList<TmdbSeason>? tmdb,
        IReadOnlyList<SeasonRequest> requests,
        IReadOnlyList<SonarrSeasonStatus> sonarr,
        DateOnly today,
        bool canGet)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(sonarr);

        var librarySeasons = library.GroupBy(e => e.SeasonNumber).ToDictionary(g => g.Key, g => g.ToList());

        // TMDB's specials never make a season, and a season with no episodes listed isn't one yet.
        var tmdbSeasons = (tmdb ?? [])
            .Where(s => s.SeasonNumber > 0 && s.Episodes.Count > 0)
            .GroupBy(s => s.SeasonNumber)
            .ToDictionary(g => g.Key, g => g.First());

        var requestBySeason = requests.GroupBy(r => r.SeasonNumber).ToDictionary(g => g.Key, g => g.Last());
        var sonarrBySeason = sonarr.GroupBy(s => s.SeasonNumber).ToDictionary(g => g.Key, g => g.Last());

        return librarySeasons.Keys.Union(tmdbSeasons.Keys)
            .Order()
            .Select(number => BuildSeason(
                number,
                librarySeasons.GetValueOrDefault(number) ?? [],
                tmdbSeasons.GetValueOrDefault(number),
                requestBySeason.GetValueOrDefault(number),
                sonarrBySeason.GetValueOrDefault(number),
                today,
                canGet))
            .ToList();
    }

    private static SeasonView BuildSeason(
        int number,
        List<LibraryEpisode> library,
        TmdbSeason? tmdb,
        SeasonRequest? request,
        SonarrSeasonStatus? sonarr,
        DateOnly today,
        bool canGet)
    {
        var tmdbEpisodes = (tmdb?.Episodes ?? []).GroupBy(e => e.EpisodeNumber).Select(g => g.First()).ToList();

        // The episode numbers the library covers (a multi-episode file covers its range), and the library episodes
        // with no number, which still count once each.
        var covered = new HashSet<int>();
        foreach (var episode in library)
        {
            if (episode.EpisodeNumber is { } first)
            {
                var last = Math.Max(first, episode.EpisodeNumberEnd ?? first);
                for (var e = first; e <= last; e++)
                {
                    covered.Add(e);
                }
            }
        }

        var unnumbered = library.Count(e => e.EpisodeNumber is null);
        var onServer = covered.Count + unnumbered;
        var aired = tmdbEpisodes.Count(e => IsAired(e, today));
        var libraryOnly = tmdb is null;
        var total = covered.Union(tmdbEpisodes.Select(e => e.EpisodeNumber)).Count() + unnumbered;

        var state = GetState(onServer, aired, libraryOnly, request, sonarr);
        var mismatch = sonarr?.Mismatch == true;
        var gettable = canGet
            && aired > onServer
            && !mismatch
            && number > 0
            && state is not (SeasonView.StateRequested or SeasonView.StateDownloading);
        var coming = state is SeasonView.StateRequested or SeasonView.StateDownloading;

        return new SeasonView(
            number,
            GetName(number, tmdb, library),
            state,
            onServer,
            libraryOnly ? onServer : aired,
            total,
            gettable,
            BuildEpisodes(number, library, tmdbEpisodes, covered, coming, today),
            request?.State == SeasonRequest.StateFailed);
    }

    private static string GetState(int onServer, int aired, bool libraryOnly, SeasonRequest? request, SonarrSeasonStatus? sonarr)
    {
        if (libraryOnly || (aired > 0 && onServer >= aired))
        {
            return SeasonView.StateOnServer;
        }

        if (request?.State == SeasonRequest.StatePending)
        {
            return SeasonView.StateRequested;
        }

        // A season with nothing aired has nothing to want or to be idle about; only a real download counts.
        switch (sonarr?.Status)
        {
            case SonarrSeasonStatus.StatusDownloading:
                return SeasonView.StateDownloading;
            case SonarrSeasonStatus.StatusWanted when aired > 0:
                return SeasonView.StateRequested;
            case SonarrSeasonStatus.StatusIdle when aired > 0:
                // Searched and settled: partly with some, not on the server with none.
                return onServer > 0 ? SeasonView.StatePartly : SeasonView.StateNotOnServer;
        }

        return onServer > 0 ? SeasonView.StatePartly : SeasonView.StateNotOnServer;
    }

    // One row per library episode, plus one per TMDB episode the library doesn't cover, by episode number (library
    // episodes without one last).
    private static List<EpisodeView> BuildEpisodes(
        int season,
        List<LibraryEpisode> library,
        List<TmdbEpisode> tmdbEpisodes,
        HashSet<int> covered,
        bool coming,
        DateOnly today)
    {
        var tmdbByNumber = tmdbEpisodes.ToDictionary(e => e.EpisodeNumber);

        var rows = library.Select(e =>
        {
            var match = e.EpisodeNumber is { } n ? tmdbByNumber.GetValueOrDefault(n) : null;
            return new EpisodeView(
                season,
                e.EpisodeNumber,
                e.Name ?? match?.Name,
                e.Overview ?? match?.Overview,
                e.AirDate ?? match?.AirDate,
                e.RuntimeMinutes ?? match?.RuntimeMinutes,
                StillUrl(match?.StillPath),
                e.ItemId,
                EpisodeView.StateOnServer);
        });

        var missing = tmdbEpisodes
            .Where(e => !covered.Contains(e.EpisodeNumber))
            .Select(e => new EpisodeView(
                season,
                e.EpisodeNumber,
                e.Name,
                e.Overview,
                e.AirDate,
                e.RuntimeMinutes,
                StillUrl(e.StillPath),
                null,
                !IsAired(e, today) ? EpisodeView.StateNotAired
                    : coming ? EpisodeView.StateComing
                    : EpisodeView.StateNotOnServer));

        return rows.Concat(missing)
            .OrderBy(e => e.EpisodeNumber is null)
            .ThenBy(e => e.EpisodeNumber)
            .ToList();
    }

    private static bool IsAired(TmdbEpisode episode, DateOnly today)
        => episode.AirDate is { } date && date <= today;

    private static string? StillUrl(string? stillPath)
        => string.IsNullOrEmpty(stillPath) ? null : StillUrlPrefix + stillPath;

    private static string GetName(int number, TmdbSeason? tmdb, List<LibraryEpisode> library)
    {
        if (!string.IsNullOrWhiteSpace(tmdb?.Name))
        {
            return tmdb.Name;
        }

        var libraryName = library.Select(e => e.SeasonName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (libraryName is not null)
        {
            return libraryName;
        }

        return number == 0 ? "Specials" : "Season " + number.ToString(CultureInfo.InvariantCulture);
    }
}
