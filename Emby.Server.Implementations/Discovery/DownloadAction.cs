using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the Download action (kind <see cref="ItemAction.RequestKind"/>). It is offered on virtual discovery films while
/// the discovery app has downloads enabled, until the film is in a library. Its state follows the film's request:
/// <see cref="StateAvailable"/> (none yet, or the last attempt failed; the only enabled state),
/// <see cref="StateRequested"/> or <see cref="StateDownloading"/>. Posting it adds the user as a requester, records the
/// request and saves the film to that user's Saved. The server never calls Radarr: the app picks requests up.
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
        [StateRequested] = "Requested",
        [StateDownloading] = "Downloading",
    };

    private readonly IDiscoveryRequestStore _requests;
    private readonly IUserListStore _lists;
    private readonly IDiscoveryFilmSync _filmSync;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadAction"/> class.
    /// </summary>
    /// <param name="requests">The download request store.</param>
    /// <param name="lists">The user list store, to save a requested film.</param>
    /// <param name="filmSync">The discovery film sync, to tell a discovery film and an owned one.</param>
    public DownloadAction(IDiscoveryRequestStore requests, IUserListStore lists, IDiscoveryFilmSync filmSync)
    {
        _requests = requests;
        _lists = lists;
        _filmSync = filmSync;
    }

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public async Task<ItemAction?> GetActionAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        if (GetDiscoveryTmdbId(item) is not { } tmdbId
            || !await _requests.GetDownloadsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var request = await _requests.GetAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        if (DiscoveryRequestDone.IsOwned(_filmSync, tmdbId))
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

        if (GetDiscoveryTmdbId(item) is not { } tmdbId)
        {
            return null;
        }

        // Under the film sync's lock, like Save, so a sync can't delete the film between the check and the request: a
        // film that is gone by now (its list rotated out and the sync ran) isn't requested. Once the request and the
        // save are recorded, the sync keeps the film.
        var request = await _filmSync.RunExclusiveAsync(
            async () =>
            {
                if (_filmSync.FindFilm(tmdbId) is not { } film)
                {
                    return await _requests.GetAsync(tmdbId, cancellationToken).ConfigureAwait(false);
                }

                var requested = await _requests.RequestAsync(tmdbId, user.Id, cancellationToken).ConfigureAwait(false);
                var saved = await _lists.GetDefaultListAsync(user.Id, cancellationToken).ConfigureAwait(false);
                var imdbId = film.GetProviderId(MetadataProvider.Imdb);
                await _lists.AddFilmAsync(user.Id, saved.Id, tmdbId, string.IsNullOrEmpty(imdbId) ? null : imdbId, cancellationToken).ConfigureAwait(false);
                return requested;
            },
            cancellationToken).ConfigureAwait(false);

        return Create(request?.State);
    }

    // No request, a failed one or a done one (its film left the library again): Download, enabled. Otherwise the
    // request's state, disabled.
    private static ItemAction Create(string? requestState)
    {
        var state = requestState switch
        {
            DiscoveryRequest.StateRequested => StateRequested,
            DiscoveryRequest.StateDownloading => StateDownloading,
            _ => StateAvailable,
        };

        return new ItemAction(ActionId, _labels[state], "download", ItemAction.RequestKind)
        {
            State = state,
            Labels = _labels,
            Enabled = state == StateAvailable,
        };
    }

    // The TMDB id of the virtual discovery film of that id; null for a library film or anything else.
    private int? GetDiscoveryTmdbId(BaseItem item)
        => item is Movie
            && int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId)
            && tmdbId > 0
            && _filmSync.FindFilm(tmdbId) is { } film
            && film.Id.Equals(item.Id)
                ? tmdbId
                : null;
}
