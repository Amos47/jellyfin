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
/// Fork: the Save toggle. It is offered on virtual discovery films and shows, and on any film or show already in one of
/// the user's lists (by TMDB id and type, so a discovery title that has since become owned can still be removed). Its
/// state is whether the title is in the user's default list, Saved; posting it adds the title there or removes it. A
/// film and a show with one TMDB id are saved apart. Lists are per user.
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

        if (!DiscoveryTitles.TryGetTmdbId(item, out var tmdbId, out var type))
        {
            return null;
        }

        var lists = await _lists.GetListsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (!IsDiscoveryTitle(item, tmdbId, type) && !lists.Any(l => l.Contains(tmdbId, type)))
        {
            return null;
        }

        return Create(lists.First(l => l.IsDefault).Contains(tmdbId, type));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetQueueAsync(User user, BaseItem item, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Guid>>([]);

    /// <inheritdoc />
    public async Task<ItemAction?> PerformAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        if (!DiscoveryTitles.TryGetTmdbId(item, out var tmdbId, out var type))
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
                if (!saved.Contains(tmdbId, type) && _filmSync.FindTitle(tmdbId, type) is null && _filmSync.FindOwnedTitle(user, tmdbId, null, type) is null)
                {
                    return false;
                }

                return await _lists.ToggleAsync(user.Id, saved.Id, tmdbId, type, string.IsNullOrEmpty(imdbId) ? null : imdbId, cancellationToken).ConfigureAwait(false) ?? false;
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

    // The virtual discovery title of its TMDB id and type, not a library item with the same id.
    private bool IsDiscoveryTitle(BaseItem item, int tmdbId, DiscoveryMediaType type)
        => _filmSync.FindTitle(tmdbId, type) is { } title && title.Id.Equals(item.Id);
}
