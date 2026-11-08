using System;
using System.Collections.Generic;
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
/// home screen picks today, the queue goes on with the trailers of the films after it in that list.
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
    private readonly ILogger<WatchTrailerAction> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTrailerAction"/> class.
    /// </summary>
    /// <param name="picks">The user's daily discovery picks.</param>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="logger">The logger.</param>
    public WatchTrailerAction(IDiscoveryUserPicks picks, IDiscoveryRowPlanner planner, ILogger<WatchTrailerAction> logger)
    {
        _picks = picks;
        _planner = planner;
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
            return GetTrailerIds(user, item);
        }

        foreach (var list in lists)
        {
            // The row items are the films as the user sees them: owned or visible only.
            var films = _planner.GetRowItems(user, list);
            var index = films.ToList().FindIndex(f => f.Item.Id.Equals(item.Id));
            if (index >= 0)
            {
                return films.Skip(index).SelectMany(f => GetTrailerIds(user, f.Item)).ToList();
            }
        }

        return GetTrailerIds(user, item);
    }

    private static IReadOnlyList<Guid> GetTrailerIds(User user, BaseItem item)
        => item.GetExtras(_trailerTypes, user).Select(t => t.Id).ToList();
}
