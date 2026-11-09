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
/// Fork: decides which discovery lists a user sees on a day and which films each shows.
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
    public async Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(rules);

        if (maxRows <= 0)
        {
            return [];
        }

        var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);
        if (!rules.RequireTrailer)
        {
            // Today's behaviour: any list with a film the user may see, picked afresh on every read.
            var visible = lists.Where(l => l.Films.Any(f => Resolve(user, f) is not null));
            var visibleSalt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
            return DiscoveryPicker.Pick(user.Id, day, visible, maxRows, visibleSalt);
        }

        var byId = lists.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var saved = await _store.GetPickSnapshotAsync(user.Id, day, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            // The saved picks hold for the day; a list deleted since is left out.
            var kept = ToLists(saved, byId, maxRows);
            if (kept.Count > 0)
            {
                return kept;
            }

            // Every saved list was deleted: the snapshot is dead, so the day is picked again.
            await _store.RemovePickSnapshotAsync(user.Id, day, saved, cancellationToken).ConfigureAwait(false);
        }

        // The salt is read before readiness is worked out, so a re-pick that lands meanwhile makes the save a no-op.
        var salt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
        var picked = GetReadyPick(user, lists, rules.MinReadyFilms, day, salt, cancellationToken);

        // Only a pick that fills maxRows is saved (in full, so any maxRows reads a prefix of it). A smaller one, empty
        // included, is worked out again on later reads (through the readiness cache), so lists that become ready
        // later that day still join it until it is full.
        if (picked.Count >= maxRows)
        {
            picked = await _store.AddPickSnapshotAsync(user.Id, day, salt, picked, cancellationToken).ConfigureAwait(false);
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
        // The generation goes first, so a count that started before this is not stored after the clear.
        Interlocked.Increment(ref _generation);
        _readiness.Clear();
    }

    private static List<DiscoveryList> ToLists(IReadOnlyList<string> listIds, Dictionary<string, DiscoveryList> byId, int maxRows)
        => listIds
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<DiscoveryList>()
            .Take(maxRows)
            .ToList();

    private IReadOnlyList<string> GetReadyPick(User user, IReadOnlyList<DiscoveryList> lists, int minReadyFilms, DateOnly day, string? salt, CancellationToken cancellationToken)
    {
        var key = new ReadinessKey(user.Id, day, salt ?? string.Empty, minReadyFilms);
        var now = _time.GetUtcNow();
        if (_readiness.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return cached.ListIds;
        }

        var generation = Interlocked.Read(ref _generation);
        var picked = PickReady(user, lists, minReadyFilms, day, salt, cancellationToken);
        if (Interlocked.Read(ref _generation) == generation)
        {
            _readiness[key] = new ReadinessEntry(picked, now + ReadinessLifetime);
            foreach (var expired in _readiness.Where(e => e.Value.Expires <= now).ToList())
            {
                _readiness.TryRemove(expired);
            }
        }

        return picked;
    }

    // The full ordered pick among the ready lists; each film is checked at most once.
    private List<string> PickReady(User user, IReadOnlyList<DiscoveryList> lists, int minReadyFilms, DateOnly day, string? salt, CancellationToken cancellationToken)
    {
        var playable = new Dictionary<int, bool>();
        var ready = new List<DiscoveryList>();
        foreach (var list in lists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = 0;
            foreach (var film in list.Films)
            {
                if (!playable.TryGetValue(film.TmdbId, out var isPlayable))
                {
                    isPlayable = ResolvePlayable(user, film) is not null;
                    playable[film.TmdbId] = isPlayable;
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
        // An owned film always plays; a virtual one only once a local trailer is attached.
        if (_sync.FindOwned(user, film.TmdbId, film.ImdbId) is { } owned)
        {
            return owned;
        }

        var virtualFilm = _sync.FindFilm(film.TmdbId);
        return virtualFilm is not null && virtualFilm.IsVisibleStandalone(user) && virtualFilm.GetExtras(_trailerTypes, user).Any()
            ? virtualFilm
            : null;
    }

    private BaseItem? Resolve(User user, DiscoveryFilmRef film)
    {
        // The owned film comes from a normal user query, so it is already filtered for the user. The virtual film is
        // checked the way the detail endpoint (GetItemById with a user) checks it, so rows and film pages agree.
        if (_sync.FindOwned(user, film.TmdbId, film.ImdbId) is { } owned)
        {
            return owned;
        }

        var virtualFilm = _sync.FindFilm(film.TmdbId);
        return virtualFilm is not null && virtualFilm.IsVisibleStandalone(user) ? virtualFilm : null;
    }

    private readonly record struct ReadinessKey(Guid UserId, DateOnly Day, string Salt, int MinReadyFilms);

    private sealed record ReadinessEntry(IReadOnlyList<string> ListIds, DateTimeOffset Expires);
}
