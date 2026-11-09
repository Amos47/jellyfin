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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the built-in Watch trailer action, on any film with a local trailer. When the film is in a list the user's
/// home screen picks today, the queue goes on with the trailers of the films after it in that list. An owned film
/// without a trailer of its own uses the trailers of its hidden discovery copy (same TMDB id, or the same IMDb id when
/// the film has no TMDB id), so a list's owned films play a trailer too. Only the action does this: the upstream
/// LocalTrailerCount and LocalTrailers stay the film's own.
/// </summary>
public sealed class WatchTrailerAction : IItemActionProvider
{
    /// <summary>
    /// The action id.
    /// </summary>
    public const string ActionId = "watch-trailer";

    private static readonly ExtraType[] _trailerTypes = [ExtraType.Trailer];

    private readonly IDiscoveryUserPicks _picks;
    private readonly IDiscoveryRowPlanner _planner;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly ILogger<WatchTrailerAction> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTrailerAction"/> class.
    /// </summary>
    /// <param name="picks">The user's daily discovery picks.</param>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="filmSync">The discovery film sync, to find an owned film's hidden copy.</param>
    /// <param name="logger">The logger.</param>
    public WatchTrailerAction(IDiscoveryUserPicks picks, IDiscoveryRowPlanner planner, IDiscoveryFilmSync filmSync, ILogger<WatchTrailerAction> logger)
    {
        _picks = picks;
        _planner = planner;
        _filmSync = filmSync;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public Task<ItemAction?> GetActionAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        ItemAction? action = item is Movie && GetTrailerIds(user, item).Count > 0
            ? new ItemAction(ActionId, "Watch trailer", "trailer", ItemAction.PlayQueueKind)
            : null;
        return Task.FromResult(action);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetQueueAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => (await GetQueueEntriesAsync(user, item, cancellationToken).ConfigureAwait(false)).Select(e => e.Id).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueEntry>> GetQueueEntriesAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        // The same day the Movie discovery rows use: the server's local date.
        var today = DateOnly.FromDateTime(DateTime.Now);
        IReadOnlyList<DiscoveryList> lists;
        try
        {
            lists = await _picks.GetPickedListsAsync(user, today, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The film's own trailers still play when the user's picks can't be worked out.
            _logger.LogWarning(ex, "Could not get the discovery picks of user {UserId}; queueing only the trailers of {ItemId}", user.Id, item.Id);
            return GetEntries(user, item);
        }

        foreach (var list in lists)
        {
            // The row items are the films as the user sees them: owned or visible only.
            var films = _planner.GetRowItems(user, list);
            var index = films.ToList().FindIndex(f => f.Item.Id.Equals(item.Id));
            if (index >= 0)
            {
                // The list's TMDB id: an owned row item comes from a light query without its provider ids.
                return films.Skip(index).SelectMany(f => GetEntries(user, f.Item, f.Film.TmdbId)).ToList();
            }
        }

        return GetEntries(user, item);
    }

    // The trailers of a film, each named after the film: the row item is the owned film when the user owns it (even
    // when the trailer is its hidden copy's), else the discovery copy.
    private List<QueueEntry> GetEntries(User user, BaseItem film, int? tmdbId = null)
    {
        var subtitle = film.ProductionYear is { } year
            ? string.Create(CultureInfo.InvariantCulture, $"Trailer \u00b7 {year}")
            : "Trailer";
        return GetTrailerIds(user, film, tmdbId).Select(id => new QueueEntry(id, film.Name, subtitle)).ToList();
    }

    private IReadOnlyList<Guid> GetTrailerIds(User user, BaseItem item, int? tmdbId = null)
    {
        // A trailer the film has itself always wins.
        var own = GetOwnTrailerIds(user, item);
        if (own.Count > 0 || item is not Movie)
        {
            return own;
        }

        // The caller has already checked that the user may see this film, so its hidden copy's trailer may play even
        // when the copy itself is hidden from the user: only trailer ids go out, never the copy.
        if (tmdbId is null && int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            tmdbId = parsed;
        }

        // No usable TMDB id: the IMDb id still finds the copy (lists may name a film by both).
        Movie? copy = tmdbId is { } id
            ? _filmSync.FindFilm(id)
            : item.GetProviderId(MetadataProvider.Imdb) is { Length: > 0 } imdbId ? _filmSync.FindFilmByImdb(imdbId) : null;
        if (copy is null || copy.Id.Equals(item.Id))
        {
            return own;
        }

        return GetOwnTrailerIds(user, copy);
    }

    private static IReadOnlyList<Guid> GetOwnTrailerIds(User user, BaseItem item)
        => item.GetExtras(_trailerTypes, user).Select(t => t.Id).ToList();
}
