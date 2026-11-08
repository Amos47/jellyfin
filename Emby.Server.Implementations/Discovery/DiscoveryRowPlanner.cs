using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: decides which discovery lists a user sees on a day and which films each shows.
/// </summary>
public sealed class DiscoveryRowPlanner : IDiscoveryRowPlanner
{
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
    public async Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (maxRows <= 0)
        {
            return [];
        }

        var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);
        var eligible = new Dictionary<string, DiscoveryList>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (list.Films.Any(f => Resolve(user, f) is not null))
            {
                eligible.TryAdd(list.Id, list);
            }
        }

        // The day's re-pick salt, if an admin asked for new picks: every caller (the rows, the picks API, the trailer
        // queue) reads it here, so they agree.
        var salt = await _store.GetPickSaltAsync(day, cancellationToken).ConfigureAwait(false);
        return DiscoveryPicker.Pick(user.Id, day, eligible.Values, maxRows, salt);
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveryRowItem> GetRowItems(User user, DiscoveryList list)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(list);

        var items = new List<DiscoveryRowItem>(list.Films.Count);
        foreach (var film in list.Films)
        {
            if (Resolve(user, film) is { } item)
            {
                items.Add(new DiscoveryRowItem(film, item));
            }
        }

        return items;
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
