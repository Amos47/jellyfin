using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.TV;

/// <summary>
/// Fork: finds what a series' rewatch marker offers next.
/// </summary>
public sealed class RewatchCandidateFinder : IRewatchCandidateFinder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IRewatchMarkerStore _store;
    private readonly ILogger<RewatchCandidateFinder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RewatchCandidateFinder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="store">The rewatch marker store.</param>
    /// <param name="logger">The logger.</param>
    public RewatchCandidateFinder(ILibraryManager libraryManager, IUserDataManager userDataManager, IRewatchMarkerStore store, ILogger<RewatchCandidateFinder> logger)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _store = store;
    }

    /// <inheritdoc />
    public (Episode Episode, DateTime Date)? GetCandidate(User user, string seriesKey, BaseItem? frontier, DtoOptions options)
    {
        ArgumentNullException.ThrowIfNull(user);

        // A broken marker file must never break Next Up and Home
        try
        {
            return FindCandidate(user, seriesKey, frontier, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find the rewatch episode for {SeriesKey}", seriesKey);
            return null;
        }
    }

    private (Episode Episode, DateTime Date)? FindCandidate(User user, string seriesKey, BaseItem? frontier, DtoOptions options)
    {
        if (_store.Get(user.Id, seriesKey) is not { } marker)
        {
            return null;
        }

        var query = RewatchQueries.Episodes(user, seriesKey);
        query.DtoOptions = options;
        var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
        var index = episodes.FindIndex(e => (e.PrimaryVersionId ?? e.Id).Equals(marker.EpisodeId));
        if (index < 0)
        {
            _store.Remove(user.Id, seriesKey);
            return null;
        }

        var candidate = IsResumable(user, episodes[index]) ? episodes[index] : episodes.ElementAtOrDefault(index + 1);
        if (RewatchRules.IsCaughtUp(EpisodePosition.From(candidate), EpisodePosition.From(frontier as Episode)))
        {
            _store.Remove(user.Id, seriesKey);
            return null;
        }

        return (candidate!, marker.Date);
    }

    private bool IsResumable(User user, Episode episode)
        => _userDataManager.GetUserData(user, episode)?.PlaybackPositionTicks > 0
            || _userDataManager.GetResumeUserData(user, episode)?.UserData.PlaybackPositionTicks > 0;
}
