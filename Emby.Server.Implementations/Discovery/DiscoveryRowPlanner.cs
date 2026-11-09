using System;
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
    private static readonly ExtraType[] _trailerTypes = [ExtraType.Trailer];

    private readonly IDiscoveryStore _store;
    private readonly IDiscoveryFilmSync _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryRowPlanner"/> class.
    /// </summary>
    /// <param name="store">The discovery store.</param>
    /// <param name="sync">The discovery film sync.</param>
    public DiscoveryRowPlanner(IDiscoveryStore store, IDiscoveryFilmSync sync)
    {
        _store = store;
        _sync = sync;
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
            var salt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
            return DiscoveryPicker.Pick(user.Id, day, visible, maxRows, salt);
        }

        var listIds = await _store.GetPickSnapshotAsync(user.Id, day, cancellationToken).ConfigureAwait(false)
            ?? await PickReadyAsync(user, lists, rules.MinReadyFilms, day, cancellationToken).ConfigureAwait(false);

        // The saved picks hold for the day; a list deleted since is left out.
        var byId = lists.ToDictionary(l => l.Id, StringComparer.Ordinal);
        return listIds
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<DiscoveryList>()
            .Take(maxRows)
            .ToList();
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

    private async Task<IReadOnlyList<string>> PickReadyAsync(User user, IReadOnlyList<DiscoveryList> lists, int minReadyFilms, DateOnly day, CancellationToken cancellationToken)
    {
        // The salt is read before readiness is worked out, so a re-pick that lands meanwhile makes the save a no-op.
        var salt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
        var ready = new List<DiscoveryList>();
        foreach (var list in lists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CountReadyFilms(user, list, minReadyFilms) >= minReadyFilms)
            {
                ready.Add(list);
            }
        }

        // The full ordered pick is saved, so any maxRows reads a prefix of it.
        var picked = DiscoveryPicker.Pick(user.Id, day, ready, ready.Count, salt).Select(l => l.Id).ToList();
        if (picked.Count == 0)
        {
            // Nothing to keep stable: the first non-empty pick of the day is the one saved.
            return picked;
        }

        return await _store.AddPickSnapshotAsync(user.Id, day, salt, picked, cancellationToken).ConfigureAwait(false);
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
}
