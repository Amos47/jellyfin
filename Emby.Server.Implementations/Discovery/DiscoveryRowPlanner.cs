using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: decides which discovery lists a user sees on a day and which titles each shows. Movie discovery picks among
/// the Movie lists and Show discovery among the Series lists; each type has its own saved picks and readiness, and a
/// title only ever resolves to a film or show of its own type.
/// </summary>
public sealed class DiscoveryRowPlanner : IDiscoveryRowPlanner
{
    /// <summary>
    /// How long a worked-out readiness (an unsaved pick) is reused before it is counted again.
    /// </summary>
    public static readonly TimeSpan ReadinessLifetime = TimeSpan.FromSeconds(60);

    private static readonly ExtraType[] _trailerTypes = [ExtraType.Trailer];

    private readonly IDiscoveryStore _store;
    private readonly IDiscoveryFilmSync _sync;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<ReadinessKey, ReadinessEntry> _readiness = new();
    private long _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryRowPlanner"/> class.
    /// </summary>
    /// <param name="store">The discovery store.</param>
    /// <param name="sync">The discovery film sync.</param>
    public DiscoveryRowPlanner(IDiscoveryStore store, IDiscoveryFilmSync sync)
        : this(store, sync, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryRowPlanner"/> class.
    /// </summary>
    /// <param name="store">The discovery store.</param>
    /// <param name="sync">The discovery film sync.</param>
    /// <param name="time">The clock the readiness cache runs on.</param>
    public DiscoveryRowPlanner(IDiscoveryStore store, IDiscoveryFilmSync sync, TimeProvider time)
    {
        _store = store;
        _sync = sync;
        _time = time;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, CancellationToken cancellationToken)
        => GetPickedListsAsync(user, maxRows, rules, day, DiscoveryMediaType.Movie, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, DiscoveryMediaType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(rules);

        if (maxRows <= 0)
        {
            return [];
        }

        // The lists of the row's type only: Movie discovery picks films, Show discovery shows.
        var lists = (await _store.GetListsAsync(cancellationToken).ConfigureAwait(false))
            .Where(l => l.ItemType == type)
            .ToList();
        if (!rules.RequireTrailer)
        {
            // Today's behaviour: any list with a film the user may see, picked afresh on every read.
            var visible = lists.Where(l => l.Films.Any(f => Resolve(user, f) is not null));
            var visibleSalt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
            return DiscoveryPicker.Pick(user.Id, day, visible, maxRows, visibleSalt);
        }

        var byId = lists.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var saved = await GetSnapshotAsync(user.Id, day, type, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            // The saved picks hold for the day; a list deleted since is left out.
            var kept = ToLists(saved, byId, maxRows);
            if (kept.Count > 0)
            {
                return kept;
            }

            // Every saved list was deleted: the snapshot is dead, so the day is picked again.
            await RemoveSnapshotAsync(user.Id, day, type, saved, cancellationToken).ConfigureAwait(false);
        }

        // The salt is read before readiness is worked out, so a re-pick that lands meanwhile makes the save a no-op.
        var salt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
        var picked = GetReadyPick(user, lists, rules.MinReadyFilms, day, type, salt, cancellationToken);

        // Only a full pick is saved (in full, so any maxRows reads a prefix of it): one that fills maxRows, or every
        // row the stored lists can ever give when that is fewer (one per group, one per ungrouped list). A smaller
        // one, empty included, is worked out again on later reads (through the readiness cache), so lists that become
        // ready later that day still join it until it is full.
        var full = Math.Min(maxRows, GetCapacity(lists));
        if (full > 0 && picked.Count >= full)
        {
            picked = await AddSnapshotAsync(user.Id, day, type, salt, picked, cancellationToken).ConfigureAwait(false);
        }

        return ToLists(picked, byId, maxRows);
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveryRowItem> GetRowItems(User user, DiscoveryList list)
        => GetRowItems(user, list, false);

    /// <inheritdoc />
    public IReadOnlyList<DiscoveryRowItem> GetRowItems(User user, DiscoveryList list, bool requireTrailer)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(list);

        var items = new List<DiscoveryRowItem>(list.Films.Count);
        foreach (var film in list.Films)
        {
            var item = requireTrailer ? ResolvePlayable(user, film) : Resolve(user, film);
            if (item is not null)
            {
                items.Add(new DiscoveryRowItem(film, item));
            }
        }

        return items;
    }

    /// <inheritdoc />
    public int CountReadyFilms(User user, DiscoveryList list)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(list);

        return CountReadyFilms(user, list, int.MaxValue);
    }

    /// <inheritdoc />
    public bool IsPlayable(User user, DiscoveryFilmRef film)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(film);

        return ResolvePlayable(user, film) is not null;
    }

    /// <inheritdoc />
    public void InvalidateReadiness()
    {
        // Entries carry the generation they were counted in; reads ignore older ones, so a count that started before
        // this is never served after it.
        Interlocked.Increment(ref _generation);
        _readiness.Clear();
    }

    private static List<DiscoveryList> ToLists(IReadOnlyList<string> listIds, Dictionary<string, DiscoveryList> byId, int maxRows)
        => listIds
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<DiscoveryList>()
            .Take(maxRows)
            .ToList();

    // The most lists a pick can hold: every list with no group, plus one per distinct group.
    private static int GetCapacity(IReadOnlyList<DiscoveryList> lists)
        => lists.Count(l => string.IsNullOrWhiteSpace(l.Group))
            + lists.Where(l => !string.IsNullOrWhiteSpace(l.Group)).Select(l => l.Group).Distinct(StringComparer.Ordinal).Count();

    private IReadOnlyList<string> GetReadyPick(User user, IReadOnlyList<DiscoveryList> lists, int minReadyFilms, DateOnly day, DiscoveryMediaType type, string? salt, CancellationToken cancellationToken)
    {
        var key = new ReadinessKey(user.Id, day, type, salt ?? string.Empty, minReadyFilms);
        var now = _time.GetUtcNow();
        // An entry from before the last invalidation is ignored, even one written after it by a count that started
        // before it, so there is no gap between checking the generation and writing the entry.
        var current = Interlocked.Read(ref _generation);
        if (_readiness.TryGetValue(key, out var cached) && cached.Expires > now && cached.Generation == current)
        {
            return cached.ListIds;
        }

        var picked = PickReady(user, lists, minReadyFilms, day, salt, cancellationToken).AsReadOnly();
        _readiness[key] = new ReadinessEntry(picked, now + ReadinessLifetime, current);
        foreach (var stale in _readiness.Where(e => e.Value.Expires <= now || e.Value.Generation < current).ToList())
        {
            _readiness.TryRemove(stale);
        }

        return picked;
    }

    // The full ordered pick among the ready lists; each film is checked at most once.
    private List<string> PickReady(User user, IReadOnlyList<DiscoveryList> lists, int minReadyFilms, DateOnly day, string? salt, CancellationToken cancellationToken)
    {
        var playable = new Dictionary<(int TmdbId, DiscoveryMediaType Type), bool>();
        var ready = new List<DiscoveryList>();
        foreach (var list in lists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = 0;
            foreach (var film in list.Films)
            {
                if (!playable.TryGetValue((film.TmdbId, film.Type), out var isPlayable))
                {
                    isPlayable = ResolvePlayable(user, film) is not null;
                    playable[(film.TmdbId, film.Type)] = isPlayable;
                }

                if (isPlayable && ++count >= minReadyFilms)
                {
                    ready.Add(list);
                    break;
                }
            }
        }

        return DiscoveryPicker.Pick(user.Id, day, ready, ready.Count, salt).Select(l => l.Id).ToList();
    }

    private int CountReadyFilms(User user, DiscoveryList list, int stopAt)
    {
        var count = 0;
        foreach (var film in list.Films)
        {
            if (ResolvePlayable(user, film) is not null && ++count >= stopAt)
            {
                break;
            }
        }

        return count;
    }

    private BaseItem? ResolvePlayable(User user, DiscoveryFilmRef film)
    {
        // An owned title always plays; a virtual one only once a local trailer is attached. Each by the ref's own type,
        // so a show never resolves to a film with the same TMDB id, nor a film to a show.
        if (_sync.FindOwnedTitle(user, film.TmdbId, film.ImdbId, film.Type) is { } owned)
        {
            return owned;
        }

        var virtualTitle = _sync.FindTitle(film.TmdbId, film.Type);
        return virtualTitle is not null && virtualTitle.IsVisibleStandalone(user) && virtualTitle.GetExtras(_trailerTypes, user).Any()
            ? virtualTitle
            : null;
    }

    private BaseItem? Resolve(User user, DiscoveryFilmRef film)
    {
        // The owned title comes from a normal user query, so it is already filtered for the user. The virtual title is
        // checked the way the detail endpoint (GetItemById with a user) checks it, so rows and title pages agree.
        if (_sync.FindOwnedTitle(user, film.TmdbId, film.ImdbId, film.Type) is { } owned)
        {
            return owned;
        }

        var virtualTitle = _sync.FindTitle(film.TmdbId, film.Type);
        return virtualTitle is not null && virtualTitle.IsVisibleStandalone(user) ? virtualTitle : null;
    }

    // The saved picks of a type. A Movie row keeps the store calls it made before shows (the Movie overloads).
    private Task<IReadOnlyList<string>?> GetSnapshotAsync(Guid userId, DateOnly day, DiscoveryMediaType type, CancellationToken cancellationToken)
        => type == DiscoveryMediaType.Movie
            ? _store.GetPickSnapshotAsync(userId, day, cancellationToken)
            : _store.GetPickSnapshotAsync(userId, day, type, cancellationToken);

    private Task<IReadOnlyList<string>> AddSnapshotAsync(Guid userId, DateOnly day, DiscoveryMediaType type, string? salt, IReadOnlyList<string> listIds, CancellationToken cancellationToken)
        => type == DiscoveryMediaType.Movie
            ? _store.AddPickSnapshotAsync(userId, day, salt, listIds, cancellationToken)
            : _store.AddPickSnapshotAsync(userId, day, type, salt, listIds, cancellationToken);

    private Task<bool> RemoveSnapshotAsync(Guid userId, DateOnly day, DiscoveryMediaType type, IReadOnlyList<string> listIds, CancellationToken cancellationToken)
        => type == DiscoveryMediaType.Movie
            ? _store.RemovePickSnapshotAsync(userId, day, listIds, cancellationToken)
            : _store.RemovePickSnapshotAsync(userId, day, type, listIds, cancellationToken);

    private readonly record struct ReadinessKey(Guid UserId, DateOnly Day, DiscoveryMediaType Type, string Salt, int MinReadyFilms);

    private sealed record ReadinessEntry(IReadOnlyList<string> ListIds, DateTimeOffset Expires, long Generation);
}
