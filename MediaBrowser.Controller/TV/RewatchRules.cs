using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.TV;

/// <summary>
/// Fork: when a play counts as a rewatch, and when a rewatch has caught up with the new episodes. The frontier is the
/// highest-numbered episode the user has played in the series.
/// </summary>
public static class RewatchRules
{
    /// <summary>
    /// How far into an already played episode a play has to get to count as a rewatch: five seconds.
    /// </summary>
    public const long MinimumRewatchTicks = 50_000_000;

    /// <summary>
    /// Gets whether playing an episode is a rewatch: it comes before the frontier.
    /// </summary>
    /// <param name="episode">The played episode.</param>
    /// <param name="frontier">The frontier.</param>
    /// <returns>Whether it's a rewatch.</returns>
    public static bool IsRewatchPlay(EpisodePosition episode, EpisodePosition? frontier)
        => frontier is { } f && episode < f;

    /// <summary>
    /// Gets whether a rewatch has caught up: there's no next episode, or it's at or past the frontier.
    /// </summary>
    /// <param name="candidate">The episode the rewatch would offer next.</param>
    /// <param name="frontier">The frontier.</param>
    /// <returns>Whether the rewatch is over.</returns>
    public static bool IsCaughtUp(EpisodePosition? candidate, EpisodePosition? frontier)
        => candidate is not { } c || (frontier is { } f && c >= f);

    /// <summary>
    /// Gets whether a user data save should move the rewatch marker to this episode. Playback saves count once an
    /// already played episode gets five seconds in; finishing counts only when the marker is already on this episode,
    /// because a first watch also ends played. API updates (offline downloads syncing) count when the episode was
    /// played before the update.
    /// </summary>
    /// <param name="reason">Why the user data was saved.</param>
    /// <param name="played">Whether the episode is played after the save.</param>
    /// <param name="previouslyPlayed">For API updates, whether it was played before.</param>
    /// <param name="positionTicks">The playback position after the save.</param>
    /// <param name="markerIsThisEpisode">Whether the series' marker is already on this episode.</param>
    /// <returns>Whether to record the marker.</returns>
    public static bool ShouldRecord(UserDataSaveReason reason, bool played, bool? previouslyPlayed, long positionTicks, bool markerIsThisEpisode)
        => reason switch
        {
            UserDataSaveReason.PlaybackProgress or UserDataSaveReason.PlaybackFinished when played && positionTicks >= MinimumRewatchTicks => true,
            UserDataSaveReason.PlaybackFinished => played && positionTicks <= 0 && markerIsThisEpisode,
            UserDataSaveReason.UpdateUserData => previouslyPlayed == true && (played || positionTicks >= MinimumRewatchTicks),
            _ => false
        };

    /// <summary>
    /// Picks one Next Up card for a series. A new episode always wins and also blocks the rewatch card when it's
    /// partly watched and so left to Continue Watching; the rewatch marker comes next, then stock rewatching.
    /// </summary>
    /// <param name="hasNewEpisode">Whether there's a new episode after the frontier.</param>
    /// <param name="newEpisodeResumable">Whether that new episode is partly watched.</param>
    /// <param name="enableResumable">Whether the request includes partly watched episodes.</param>
    /// <param name="hasRewatchCandidate">Whether the rewatch marker offers an episode.</param>
    /// <param name="includeStockRewatching">Whether the request asks for stock rewatching.</param>
    /// <returns>The card to show.</returns>
    public static NextUpPick Pick(bool hasNewEpisode, bool newEpisodeResumable, bool enableResumable, bool hasRewatchCandidate, bool includeStockRewatching)
    {
        if (hasNewEpisode)
        {
            return enableResumable || !newEpisodeResumable ? NextUpPick.NewEpisode : NextUpPick.None;
        }

        if (hasRewatchCandidate)
        {
            return NextUpPick.Rewatch;
        }

        return includeStockRewatching ? NextUpPick.StockRewatch : NextUpPick.None;
    }
}
