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

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: the Save toggle. It is offered on virtual discovery films, and on any film already in one of the user's lists
/// (by TMDB id, so a discovery film that has since become owned can still be removed). Its state is whether the film is
/// in the user's default list, Saved; posting it adds the film there or removes it. Lists are per user.
/// </summary>
public sealed class SaveAction : IItemActionProvider
{
    /// <summary>
    /// The action id.
    /// </summary>
    public const string ActionId = "save";

    private static readonly IReadOnlyDictionary<string, string> _labels = new Dictionary<string, string>
    {
        [ItemAction.StateOn] = "Saved",
        [ItemAction.StateOff] = "Save",
    };

    private readonly IUserListStore _lists;
    private readonly IDiscoveryFilmSync _filmSync;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveAction"/> class.
    /// </summary>
    /// <param name="lists">The user list store.</param>
    /// <param name="filmSync">The discovery film sync, to tell a discovery film.</param>
    public SaveAction(IUserListStore lists, IDiscoveryFilmSync filmSync)
    {
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

        if (GetTmdbId(item) is not { } tmdbId)
        {
            return null;
        }

        var lists = await _lists.GetListsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (!IsDiscoveryFilm(item, tmdbId) && !lists.Any(l => l.Contains(tmdbId)))
        {
            return null;
        }

        return Create(lists.First(l => l.IsDefault).Contains(tmdbId));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetQueueAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Guid>>([]);

    /// <inheritdoc />
    public async Task<ItemAction?> PerformAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        if (GetTmdbId(item) is not { } tmdbId)
        {
            return null;
        }

        var imdbId = item.GetProviderId(MetadataProvider.Imdb);

        // Under the film sync's lock, so a sync can't delete the film between the check and the save: a film that is
        // gone by now (its list rotated out and the sync ran) isn't saved. Saved is read inside the lock too, so the
        // check sees any save or removal that finished first. The toggle itself is atomic in the store.
        var on = await _filmSync.RunExclusiveAsync(
            async () =>
            {
                var saved = await _lists.GetDefaultListAsync(user.Id, cancellationToken).ConfigureAwait(false);
                if (!saved.Contains(tmdbId) && _filmSync.FindFilm(tmdbId) is null && _filmSync.FindOwned(user, tmdbId, null) is null)
                {
                    return false;
                }

                return await _lists.ToggleAsync(user.Id, saved.Id, tmdbId, string.IsNullOrEmpty(imdbId) ? null : imdbId, cancellationToken).ConfigureAwait(false) ?? false;
            },
            cancellationToken).ConfigureAwait(false);

        // The new state, even when the action is no longer offered (an owned film taken out of its last list), so
        // the app can show it until it reloads the actions.
        return Create(on);
    }

    private static ItemAction Create(bool on)
        => new(ActionId, _labels[on ? ItemAction.StateOn : ItemAction.StateOff], "bookmark", ItemAction.ToggleKind)
        {
            State = on ? ItemAction.StateOn : ItemAction.StateOff,
            Labels = _labels,
            Enabled = true,
        };

    private static int? GetTmdbId(BaseItem item)
        => item is Movie && int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId) && tmdbId > 0
            ? tmdbId
            : null;

    // The virtual discovery film of its TMDB id, not a library film with the same id.
    private bool IsDiscoveryFilm(BaseItem item, int tmdbId)
        => _filmSync.FindFilm(tmdbId) is { } film && film.Id.Equals(item.Id);
}
