using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the Download action (kind <see cref="ItemAction.RequestKind"/>). It is offered on virtual discovery films while
/// the discovery app has film downloads enabled, and on virtual discovery shows while it has show downloads enabled
/// (each type its own setting), until the title is in a library (a film for a film, a show for a show). Requests are
/// keyed by TMDB id and type. Its state follows the film's request:
/// <see cref="StateAvailable"/> (none yet), <see cref="StateFailed"/> (the last attempt failed), both enabled, or
/// <see cref="StateRequested"/> or <see cref="StateDownloading"/>, disabled for everyone (anyone else uses Save).
/// Posting it adds the user as a requester, records the request and saves the film to that user's Saved. The action
/// never says who asked. The server never calls Radarr: the app picks requests up.
/// <para>
/// On a show (virtual or in a library) it is "Get series": offered while the show page
/// (<see cref="IShowSeasonsService"/>) has a season to get, or a season on its way. Its state follows the seasons:
/// while any season can be got, <see cref="StateFailed"/> if one of those last failed, else
/// <see cref="StateAvailable"/> (both enabled); with none left to get, <see cref="StateDownloading"/> if any is
/// downloading, else <see cref="StateRequested"/> (both disabled). A virtual show's series request (older apps) only
/// counts while the show has no season requests. Posting it requests
/// every season that can be got; on a virtual discovery show it also makes today's series request and saves the show,
/// so older apps keep working.
/// </para>
/// </summary>
public sealed class DownloadAction : IItemActionProvider
{
    /// <summary>
    /// The action id.
    /// </summary>
    public const string ActionId = "download";

    /// <summary>
    /// The state of a film that can be requested.
    /// </summary>
    public const string StateAvailable = "available";

    /// <summary>
    /// The state of a film whose last download attempt failed; it can be requested again.
    /// </summary>
    public const string StateFailed = "failed";

    /// <summary>
    /// The state of a film requested and not yet picked up by the app.
    /// </summary>
    public const string StateRequested = "requested";

    /// <summary>
    /// The state of a film the app is downloading.
    /// </summary>
    public const string StateDownloading = "downloading";

    private static readonly IReadOnlyDictionary<string, string> _labels = new Dictionary<string, string>
    {
        [StateAvailable] = "Download",
        [StateFailed] = "Retry download",
        [StateRequested] = "Requested",
        [StateDownloading] = "Downloading",
    };

    private static readonly IReadOnlyDictionary<string, string> _seriesLabels = new Dictionary<string, string>
    {
        [StateAvailable] = "Get series",
        [StateFailed] = "Retry",
        [StateRequested] = "Requested",
        [StateDownloading] = "Getting…",
    };

    private readonly IDiscoveryRequestStore _requests;
    private readonly IUserListStore _lists;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly IShowSeasonsService _showSeasons;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadAction"/> class.
    /// </summary>
    /// <param name="requests">The download request store.</param>
    /// <param name="lists">The user list store, to save a requested film.</param>
    /// <param name="filmSync">The discovery film sync, to tell a discovery film and an owned one.</param>
    /// <param name="showSeasons">The show page service, for a show's seasons.</param>
    public DownloadAction(IDiscoveryRequestStore requests, IUserListStore lists, IDiscoveryFilmSync filmSync, IShowSeasonsService showSeasons)
    {
        _requests = requests;
        _lists = lists;
        _filmSync = filmSync;
        _showSeasons = showSeasons;
    }

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public async Task<ItemAction?> GetActionAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        if (item is Series series)
        {
            return await GetSeriesActionAsync(user, series, cancellationToken).ConfigureAwait(false);
        }

        if (GetDiscoveryTmdbId(item) is not { } title
            || !await GetDownloadsEnabledAsync(title.Type, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var (tmdbId, type) = title;

        var request = await _requests.GetAsync(tmdbId, type, cancellationToken).ConfigureAwait(false);
        if (DiscoveryRequestDone.IsOwned(_filmSync, tmdbId, type))
        {
            // In a library now: nothing to download, and the request (if any) is done.
            await DiscoveryRequestDone.CheckAsync(_requests, _filmSync, request, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return Create(request?.State);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetQueueAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Guid>>([]);

    /// <inheritdoc />
    public async Task<ItemAction?> PerformAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        if (item is Series series)
        {
            return await PerformSeriesAsync(user, series, cancellationToken).ConfigureAwait(false);
        }

        if (GetDiscoveryTmdbId(item) is not { } title)
        {
            return null;
        }

        var (tmdbId, type) = title;

        // Under the film sync's lock, like Save, so a sync can't delete the film between the check and the request: a
        // film that is gone by now (its list rotated out and the sync ran) isn't requested, and nothing is performed
        // (null: the controller answers 400). Once the request and the save are recorded, the sync keeps the film.
        var request = await _filmSync.RunExclusiveAsync(
            async () =>
            {
                if (_filmSync.FindTitle(tmdbId, type) is not { } film)
                {
                    return null;
                }

                // Requested, downloading: nothing to do, the button is disabled. Answer with the current state.
                var existing = await _requests.GetAsync(tmdbId, type, cancellationToken).ConfigureAwait(false);
                if (Create(existing?.State).Enabled != true)
                {
                    return existing;
                }

                var requested = await _requests.RequestAsync(tmdbId, type, user.Id, cancellationToken).ConfigureAwait(false);
                var saved = await _lists.GetDefaultListAsync(user.Id, cancellationToken).ConfigureAwait(false);
                var imdbId = film.GetProviderId(MetadataProvider.Imdb);
                await _lists.AddFilmAsync(user.Id, saved.Id, tmdbId, type, string.IsNullOrEmpty(imdbId) ? null : imdbId, cancellationToken).ConfigureAwait(false);
                return requested;
            },
            cancellationToken).ConfigureAwait(false);

        return request is null ? null : Create(request.State);
    }

    // Get series: null when the show has nothing to get and nothing on its way.
    private async Task<ItemAction?> GetSeriesActionAsync(User user, Series series, CancellationToken cancellationToken)
    {
        if (!await _requests.GetDownloadsEnabledAsync(DiscoveryMediaType.Series, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // A virtual discovery show keeps its series request (older apps) and goes once the show is in a library: the
        // library show's page carries Get series from then on.
        DiscoveryRequest? seriesRequest = null;
        if (GetDiscoveryTmdbId(series) is { } title)
        {
            seriesRequest = await _requests.GetAsync(title.TmdbId, DiscoveryMediaType.Series, cancellationToken).ConfigureAwait(false);
            if (DiscoveryRequestDone.IsOwned(_filmSync, title.TmdbId, DiscoveryMediaType.Series))
            {
                await DiscoveryRequestDone.CheckAsync(_requests, _filmSync, seriesRequest, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var page = await _showSeasons.GetPageAsync(user, series, cancellationToken).ConfigureAwait(false);
        return CreateSeries(page, seriesRequest?.State);
    }

    private async Task<ItemAction?> PerformSeriesAsync(User user, Series series, CancellationToken cancellationToken)
    {
        if (await GetSeriesActionAsync(user, series, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return null;
        }

        // Requested, downloading: nothing to do, the button is disabled. Answer with the current state.
        if (current.Enabled != true)
        {
            return current;
        }

        var discovery = GetDiscoveryTmdbId(series) is not null;
        var result = await _showSeasons.GetSeasonsAsync(user, series, null, requestDiscoverySeries: discovery, cancellationToken).ConfigureAwait(false);
        return result == ShowSeasonsGetResult.Requested
            ? await GetSeriesActionAsync(user, series, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static ItemAction? CreateSeries(ShowSeasonsPage page, string? seriesRequestState)
    {
        // Once the show has season requests, the seasons decide; the series request older apps made only counts
        // without them.
        var legacy = page.HasSeasonRequests ? null : seriesRequestState;
        var downloading = page.Seasons.Any(s => s.State == SeasonView.StateDownloading);
        var requested = page.Seasons.Any(s => s.State == SeasonView.StateRequested);
        string state;
        if (legacy is DiscoveryRequest.StateDownloading or DiscoveryRequest.StateRequested)
        {
            state = legacy == DiscoveryRequest.StateDownloading || downloading ? StateDownloading : StateRequested;
        }
        else if (page.CanGetSeries)
        {
            // A season can be got: Get series stays available, whatever the other seasons are doing.
            var failed = legacy == DiscoveryRequest.StateFailed || page.Seasons.Any(s => s.CanGet && s.RequestFailed);
            state = failed ? StateFailed : StateAvailable;
        }
        else if (downloading || requested)
        {
            state = downloading ? StateDownloading : StateRequested;
        }
        else
        {
            return null;
        }

        return new ItemAction(ActionId, _seriesLabels[state], "download", ItemAction.RequestKind)
        {
            State = state,
            Labels = _seriesLabels,
            Enabled = state is StateAvailable or StateFailed,
        };
    }

    // No request, or a done one (its film left the library again): Download. A failed one: Retry download. Both
    // enabled. Otherwise the request's state, disabled.
    private static ItemAction Create(string? requestState)
    {
        var state = requestState switch
        {
            DiscoveryRequest.StateRequested => StateRequested,
            DiscoveryRequest.StateDownloading => StateDownloading,
            DiscoveryRequest.StateFailed => StateFailed,
            _ => StateAvailable,
        };

        return new ItemAction(ActionId, _labels[state], "download", ItemAction.RequestKind)
        {
            State = state,
            Labels = _labels,
            Enabled = state is StateAvailable or StateFailed,
        };
    }

    // The TMDB id and type of the virtual discovery title of that id; null for a library item or anything else.
    private (int TmdbId, DiscoveryMediaType Type)? GetDiscoveryTmdbId(BaseItem item)
        => DiscoveryTitles.TryGetTmdbId(item, out var tmdbId, out var type)
            && _filmSync.FindTitle(tmdbId, type) is { } title
            && title.Id.Equals(item.Id)
                ? (tmdbId, type)
                : null;

    // Films keep the setting call they had before shows.
    private Task<bool> GetDownloadsEnabledAsync(DiscoveryMediaType type, CancellationToken cancellationToken)
        => type == DiscoveryMediaType.Movie
            ? _requests.GetDownloadsEnabledAsync(cancellationToken)
            : _requests.GetDownloadsEnabledAsync(type, cancellationToken);
}
