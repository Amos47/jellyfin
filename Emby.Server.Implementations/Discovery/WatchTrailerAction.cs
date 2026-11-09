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
/// Fork: the built-in Watch trailer action, on any film or show with a local trailer (a show's, like a film's, goes on
/// through the Show discovery picks; its hidden copy is a show of the same TMDB id). When the film is in a list the user's
/// home screen picks today, the queue goes on with the trailers of the films after it in that list. An owned film
/// without a trailer of its own uses the trailers of its hidden discovery copy (same TMDB id, or the same IMDb id when
/// the film has no TMDB id), so a list's owned films play a trailer too. Only the action does this: the upstream
/// LocalTrailerCount and LocalTrailers stay the film's own.
/// <para>On a list page it is Watch trailers: the trailers of every playable film of the list, in list order. A film
/// queue asked for with a list (a film opened from a list page) goes on through that list instead of today's picks.
/// Every entry names the film it belongs to: the owned film when the user owns it, else the discovery copy.</para>
/// </summary>
public sealed class WatchTrailerAction : IItemActionProvider, IDiscoveryListActionProvider
{
    /// <summary>
    /// The action id.
    /// </summary>
    public const string ActionId = "watch-trailer";

    /// <summary>
    /// The list action id.
    /// </summary>
    public const string ListId = "watch-trailers";

    private static readonly ExtraType[] _trailerTypes = [ExtraType.Trailer];

    private readonly IDiscoveryUserPicks _picks;
    private readonly IDiscoveryRowPlanner _planner;
    private readonly IDiscoveryFilmSync _filmSync;
    private readonly IDiscoveryStore _store;
    private readonly IUserListStore _userLists;
    private readonly ILogger<WatchTrailerAction> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTrailerAction"/> class.
    /// </summary>
    /// <param name="picks">The user's daily discovery picks.</param>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="filmSync">The discovery film sync, to find an owned film's hidden copy.</param>
    /// <param name="store">The discovery store, for the list a queue was started from.</param>
    /// <param name="userLists">The user list store, for a user list a queue was started from.</param>
    /// <param name="logger">The logger.</param>
    public WatchTrailerAction(IDiscoveryUserPicks picks, IDiscoveryRowPlanner planner, IDiscoveryFilmSync filmSync, IDiscoveryStore store, IUserListStore userLists, ILogger<WatchTrailerAction> logger)
    {
        _picks = picks;
        _planner = planner;
        _filmSync = filmSync;
        _store = store;
        _userLists = userLists;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public string ListActionId => ListId;

    /// <inheritdoc />
    public Task<ItemAction?> GetActionAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        ItemAction? action = DiscoveryTitles.GetMediaType(item) is not null && GetTrailerIds(user, item).Count > 0
            ? new ItemAction(ActionId, "Watch trailer", "trailer", ItemAction.PlayQueueKind)
            : null;
        return Task.FromResult(action);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetQueueAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => (await GetQueueEntriesAsync(user, item, cancellationToken).ConfigureAwait(false)).Select(e => e.Id).ToList();

    /// <inheritdoc />
    public Task<IReadOnlyList<QueueEntry>> GetQueueEntriesAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => GetQueueEntriesAsync(user, item, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueEntry>> GetQueueEntriesAsync(User user, BaseItem item, string? listId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        // Started from a list (its page, or a film opened from it): the queue goes on through that list's playable
        // films, the ones its page shows. A film that isn't one of them gets the queue it would get without a list. A
        // user list (user:{guid}) counts only for its owner.
        if (!string.IsNullOrEmpty(listId)
            && await DiscoveryPageLists.GetAsync(_store, _userLists, user, listId, cancellationToken).ConfigureAwait(false) is { } startList
            && GetFrom(user, _planner.GetRowItems(user, startList, true), item) is { } fromList)
        {
            return fromList;
        }

        // The same day the discovery rows use: the server's local date. A show goes on through the Show discovery
        // picks, a film through the Movie discovery ones.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var type = DiscoveryTitles.GetMediaType(item) ?? DiscoveryMediaType.Movie;
        IReadOnlyList<DiscoveryList> lists;
        try
        {
            lists = type == DiscoveryMediaType.Movie
                ? await _picks.GetPickedListsAsync(user, today, cancellationToken).ConfigureAwait(false)
                : await _picks.GetPickedListsAsync(user, today, type, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The film's own trailers still play when the user's picks can't be worked out.
            _logger.LogWarning(ex, "Could not get the discovery picks of user {UserId}; queueing only the trailers of {ItemId}", user.Id, item.Id);
            return GetEntries(user, item, filmId: GetFilmId(user, item));
        }

        foreach (var list in lists)
        {
            // The row items are the films as the user sees them: owned or visible only.
            if (GetFrom(user, _planner.GetRowItems(user, list), item) is { } fromPick)
            {
                return fromPick;
            }
        }

        return GetEntries(user, item, filmId: GetFilmId(user, item));
    }

    /// <inheritdoc />
    public Task<ItemAction?> GetListActionAsync(User user, DiscoveryList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(list);

        // Shown when any playable film of the list has a trailer to play.
        var playable = _planner.GetRowItems(user, list, true);
        ItemAction? action = playable.Any(f => GetTrailerIds(user, f.Item, f.Film.TmdbId, f.Film.Type).Count > 0)
            ? new ItemAction(ListId, "Watch trailers", "trailer", ItemAction.PlayQueueKind)
            : null;
        return Task.FromResult(action);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueueEntry>> GetListQueueEntriesAsync(User user, DiscoveryList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(list);

        // The list page's films (the playable ones), in list order.
        IReadOnlyList<QueueEntry> entries = GetEntries(user, _planner.GetRowItems(user, list, true));
        return Task.FromResult(entries);
    }

    // The entries from the item on, when it is one of the films; else null.
    private List<QueueEntry>? GetFrom(User user, IReadOnlyList<DiscoveryRowItem> films, BaseItem item)
    {
        var index = films.ToList().FindIndex(f => f.Item.Id.Equals(item.Id));
        return index >= 0 ? GetEntries(user, films.Skip(index)) : null;
    }

    // The list's TMDB id: an owned row item comes from a light query without its provider ids. The row item is the
    // film the entries belong to: the owned film when the user owns it, else the discovery copy.
    private List<QueueEntry> GetEntries(User user, IEnumerable<DiscoveryRowItem> films)
        => films.SelectMany(f => GetEntries(user, f.Item, f.Film.TmdbId, f.Item.Id, f.Film.Type)).ToList();

    // The trailers of a film, each named after the film (even when the trailer is its hidden copy's).
    private List<QueueEntry> GetEntries(User user, BaseItem film, int? tmdbId = null, Guid? filmId = null, DiscoveryMediaType? type = null)
    {
        var subtitle = film.ProductionYear is { } year
            ? string.Create(CultureInfo.InvariantCulture, $"Trailer \u00b7 {year}")
            : "Trailer";
        var owner = filmId ?? film.Id;
        return GetTrailerIds(user, film, tmdbId, type).Select(id => new QueueEntry(id, film.Name, subtitle, owner)).ToList();
    }

    // The film a queue of the item's own trailers belongs to: the item, unless it is the discovery copy of a film the
    // user owns, in which case the owned film wins.
    private Guid GetFilmId(User user, BaseItem item)
    {
        if (DiscoveryTitles.TryGetTmdbId(item, out var tmdbId, out var type)
            && _filmSync.FindTitle(tmdbId, type) is { } copy
            && copy.Id.Equals(item.Id)
            && _filmSync.FindOwnedTitle(user, tmdbId, item.GetProviderId(MetadataProvider.Imdb), type) is { } owned)
        {
            return owned.Id;
        }

        return item.Id;
    }

    private IReadOnlyList<Guid> GetTrailerIds(User user, BaseItem item, int? tmdbId = null, DiscoveryMediaType? type = null)
    {
        // A trailer the title has itself always wins. Only films and shows borrow their copy's, each from a copy of
        // its own type.
        var own = GetOwnTrailerIds(user, item);
        if (own.Count > 0 || DiscoveryTitles.GetMediaType(item) is not { } itemType)
        {
            return own;
        }

        var copyType = type ?? itemType;

        // The caller has already checked that the user may see this film, so its hidden copy's trailer may play even
        // when the copy itself is hidden from the user: only trailer ids go out, never the copy.
        if (tmdbId is null && int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            tmdbId = parsed;
        }

        // No usable TMDB id: the IMDb id still finds the copy (lists may name a film by both).
        var copy = tmdbId is { } id
            ? _filmSync.FindTitle(id, copyType)
            : item.GetProviderId(MetadataProvider.Imdb) is { Length: > 0 } imdbId ? _filmSync.FindTitleByImdb(imdbId, copyType) : null;
        if (copy is null || copy.Id.Equals(item.Id))
        {
            return own;
        }

        return GetOwnTrailerIds(user, copy);
    }

    private static IReadOnlyList<Guid> GetOwnTrailerIds(User user, BaseItem item)
        => item.GetExtras(_trailerTypes, user).Select(t => t.Id).ToList();
}
