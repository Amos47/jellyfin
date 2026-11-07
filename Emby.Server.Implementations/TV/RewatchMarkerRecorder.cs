using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.TV;

/// <summary>
/// Fork: moves a series' rewatch marker when the user replays an episode before their newest watched one.
/// </summary>
public sealed class RewatchMarkerRecorder : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IRewatchMarkerStore _store;
    private readonly ILogger<RewatchMarkerRecorder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RewatchMarkerRecorder"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="store">The rewatch marker store.</param>
    /// <param name="logger">The logger.</param>
    public RewatchMarkerRecorder(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IRewatchMarkerStore store,
        ILogger<RewatchMarkerRecorder> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            Record(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record a rewatch marker");
        }
    }

    private void Record(UserDataSaveEventArgs e)
    {
        if (e.Item is not Episode episode
            || EpisodePosition.From(episode) is not { } position
            || string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey)
            || e.UserData is null)
        {
            return;
        }

        var seriesKey = episode.SeriesPresentationUniqueKey;
        var episodeId = episode.PrimaryVersionId ?? episode.Id;
        var markerIsThisEpisode = _store.Get(e.UserId, seriesKey)?.EpisodeId.Equals(episodeId) == true;
        var data = e.UserData;
        if (!RewatchRules.ShouldRecord(e.SaveReason, data.Played, e.PreviouslyPlayed, data.PlaybackPositionTicks, markerIsThisEpisode))
        {
            return;
        }

        var user = _userManager.GetUserById(e.UserId);
        if (user is null)
        {
            return;
        }

        var frontier = EpisodePosition.From(_libraryManager.GetItemList(RewatchQueries.Frontier(user, seriesKey)).FirstOrDefault() as Episode);
        if (!RewatchRules.IsRewatchPlay(position, frontier))
        {
            return;
        }

        var date = e.SaveReason == UserDataSaveReason.UpdateUserData ? data.LastPlayedDate ?? DateTime.UtcNow : DateTime.UtcNow;
        _store.Set(e.UserId, new RewatchMarker(seriesKey, episodeId, date));
    }
}
