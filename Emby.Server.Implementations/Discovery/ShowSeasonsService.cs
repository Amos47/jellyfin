using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the show page's seasons (<see cref="ShowSeasons.Build"/> over the library's real episodes, TMDB's seasons
/// from the server's own provider, the season requests and Sonarr's season statuses), and Get season / Get series.
/// TMDB failing or timing out never fails the page: it shows the library's seasons only. Nothing is ever added to the
/// library.
/// </summary>
public sealed class ShowSeasonsService : IShowSeasonsService
{
    /// <summary>
    /// How long the page waits for TMDB before it shows the library's seasons only.
    /// </summary>
    public static readonly TimeSpan DefaultTmdbTimeout = TimeSpan.FromSeconds(15);

    private readonly ITmdbSeasonSource _tmdb;
    private readonly ISeasonStore _seasons;
    private readonly IDiscoveryRequestStore _requests;
    private readonly IUserListStore _lists;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly ILogger<ShowSeasonsService> _logger;
    private readonly Func<User, Series, IReadOnlyList<LibraryEpisode>> _library;
    private readonly Func<Series, (string? Language, string? CountryCode)> _metadataLanguage;
    private readonly Func<DateOnly> _today;
    private readonly TimeSpan _tmdbTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowSeasonsService"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB season source.</param>
    /// <param name="seasons">The season request and Sonarr status store.</param>
    /// <param name="requests">The download request store, for the show downloads setting and series requests.</param>
    /// <param name="lists">The user list store, to save a discovery show that is got.</param>
    /// <param name="filmSync">The discovery film sync, to tell a virtual discovery show.</param>
    /// <param name="logger">The logger.</param>
    public ShowSeasonsService(
        ITmdbSeasonSource tmdb,
        ISeasonStore seasons,
        IDiscoveryRequestStore requests,
        IUserListStore lists,
        IDiscoveryFilmSync filmSync,
        ILogger<ShowSeasonsService> logger)
        : this(
            tmdb,
            seasons,
            requests,
            lists,
            filmSync,
            logger,
            ReadLibrary,
            () => DateOnly.FromDateTime(DateTime.Now),
            DefaultTmdbTimeout,
            series => (series.GetPreferredMetadataLanguage(), series.GetPreferredMetadataCountryCode()))
    {
    }

    internal ShowSeasonsService(
        ITmdbSeasonSource tmdb,
        ISeasonStore seasons,
        IDiscoveryRequestStore requests,
        IUserListStore lists,
        IDiscoveryFilmSync filmSync,
        ILogger<ShowSeasonsService> logger,
        Func<User, Series, IReadOnlyList<LibraryEpisode>> library,
        Func<DateOnly> today,
        TimeSpan tmdbTimeout,
        Func<Series, (string? Language, string? CountryCode)>? metadataLanguage = null)
    {
        _tmdb = tmdb;
        _seasons = seasons;
        _requests = requests;
        _lists = lists;
        _filmSync = filmSync;
        _logger = logger;
        _library = library;
        _today = today;
        _tmdbTimeout = tmdbTimeout;
        _metadataLanguage = metadataLanguage ?? (_ => (null, null));
    }

    /// <inheritdoc />
    public async Task<ShowSeasonsPage> GetPageAsync(User user, Series series, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(series);

        var today = _today();
        if (GetTmdbId(series) is not { } tmdbId)
        {
            return ToPage(null, ShowSeasons.Build(_library(user, series), null, [], [], today, canGet: false));
        }

        // The virtual discovery copy of a show the user has in a library shows that show's episodes, so nothing on
        // the server is offered again.
        var librarySeries = series;
        if (IsDiscoverySeries(series, tmdbId))
        {
            var imdbId = series.GetProviderId(MetadataProvider.Imdb);
            if (_filmSync.FindOwnedSeries(user, tmdbId, string.IsNullOrEmpty(imdbId) ? null : imdbId) is Series owned)
            {
                librarySeries = owned;
            }
        }

        var library = _library(user, librarySeries);

        var tmdb = await GetTmdbAsync(series, tmdbId, ct).ConfigureAwait(false);
        if (tmdb is null)
        {
            // Library only: every season on the server, nothing to get.
            return ToPage(tmdbId, ShowSeasons.Build(library, null, [], [], today, canGet: false));
        }

        var canGet = await _requests.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, ct).ConfigureAwait(false);
        var requests = await _seasons.GetAsync(tmdbId, ct).ConfigureAwait(false);
        var sonarr = await _seasons.GetSonarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
        return ToPage(tmdbId, ShowSeasons.Build(library, tmdb, requests, sonarr, today, canGet));
    }

    /// <inheritdoc />
    public async Task<ShowSeasonsGetResult> GetSeasonsAsync(User user, Series series, IReadOnlyCollection<int>? seasonNumbers, bool requestDiscoverySeries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(series);

        if (GetTmdbId(series) is not { } tmdbId)
        {
            return ShowSeasonsGetResult.NoTmdbId;
        }

        var page = await GetPageAsync(user, series, ct).ConfigureAwait(false);

        // Only what the page offers: never specials or a negative season, never one on the server or on its way.
        var wanted = page.Seasons
            .Where(s => s.CanGet && s.SeasonNumber > 0 && (seasonNumbers is null || seasonNumbers.Contains(s.SeasonNumber)))
            .Select(s => s.SeasonNumber)
            .ToList();
        if (wanted.Count == 0)
        {
            return ShowSeasonsGetResult.NotGettable;
        }

        if (!IsDiscoverySeries(series, tmdbId))
        {
            await _seasons.RequestAsync(tmdbId, wanted, user.Id, ct).ConfigureAwait(false);
            return ShowSeasonsGetResult.Requested;
        }

        // Under the film sync's lock, as Download and Save: a show the sync deleted meanwhile isn't requested; once
        // saved (and requested), the sync keeps it.
        return await _filmSync.RunExclusiveAsync(
            async () =>
            {
                if (_filmSync.FindSeries(tmdbId) is not { } show || !show.Id.Equals(series.Id))
                {
                    return ShowSeasonsGetResult.NotGettable;
                }

                if (requestDiscoverySeries)
                {
                    await _requests.RequestAsync(tmdbId, DiscoveryMediaType.Series, user.Id, ct).ConfigureAwait(false);
                }

                await _seasons.RequestAsync(tmdbId, wanted, user.Id, ct).ConfigureAwait(false);
                var saved = await _lists.GetDefaultListAsync(user.Id, ct).ConfigureAwait(false);
                var imdbId = show.GetProviderId(MetadataProvider.Imdb);
                await _lists.AddFilmAsync(user.Id, saved.Id, tmdbId, DiscoveryMediaType.Series, string.IsNullOrEmpty(imdbId) ? null : imdbId, ct).ConfigureAwait(false);
                return ShowSeasonsGetResult.Requested;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps the library's items of a show to its episodes on the server: real episodes with a season number, without
    /// missing or virtual ones (a pathless episode is a missing one).
    /// </summary>
    /// <param name="items">The items.</param>
    /// <returns>The episodes.</returns>
    internal static IReadOnlyList<LibraryEpisode> ToLibraryEpisodes(IEnumerable<BaseItem> items)
        => items
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem && !string.IsNullOrEmpty(e.Path) && e.ParentIndexNumber is not null)
            .Select(e => new LibraryEpisode(
                e.Id,
                e.ParentIndexNumber!.Value,
                e.IndexNumber,
                e.IndexNumberEnd,
                e.Name,
                e.Overview,
                e.PremiereDate is { } date ? DateOnly.FromDateTime(date) : null,
                e.RunTimeTicks is { } ticks ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes) : null,
                e.SeasonName))
            .ToList();

    private static IReadOnlyList<LibraryEpisode> ReadLibrary(User user, Series series)
        => ToLibraryEpisodes(series.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: false));

    private static ShowSeasonsPage ToPage(int? tmdbId, IReadOnlyList<SeasonView> seasons)
    {
        var gettable = seasons.Count(s => s.CanGet);
        return new ShowSeasonsPage(tmdbId, gettable > 0, gettable, seasons);
    }

    private static int? GetTmdbId(Series series)
        => int.TryParse(series.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    // The virtual discovery show of that id, not a library show with the same id.
    private bool IsDiscoverySeries(Series series, int tmdbId)
        => _filmSync.FindSeries(tmdbId) is { } show && show.Id.Equals(series.Id);

    private async Task<IReadOnlyList<TmdbSeason>?> GetTmdbAsync(Series series, int tmdbId, CancellationToken ct)
    {
        var (language, countryCode) = _metadataLanguage(series);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_tmdbTimeout);
        try
        {
            // WaitAsync holds the cap even when the source ignores the token (TmdbClientManager's client config fetch
            // takes none).
            return await _tmdb.GetSeasonsAsync(tmdbId, language, countryCode, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Show page: TMDB seasons of {TmdbId} failed; showing the library's seasons only", tmdbId);
            return null;
        }
    }
}
