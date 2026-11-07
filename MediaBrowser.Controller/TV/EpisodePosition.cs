using System;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.TV;

/// <summary>
/// Fork: where an episode sits in its series, ordered by season, then episode.
/// </summary>
/// <param name="Season">The season number.</param>
/// <param name="Episode">The episode number.</param>
public readonly record struct EpisodePosition(int Season, int Episode) : IComparable<EpisodePosition>
{
    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The left position.</param>
    /// <param name="right">The right position.</param>
    /// <returns>Whether left comes first.</returns>
    public static bool operator <(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) < 0;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The left position.</param>
    /// <param name="right">The right position.</param>
    /// <returns>Whether left comes after.</returns>
    public static bool operator >(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) > 0;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The left position.</param>
    /// <param name="right">The right position.</param>
    /// <returns>Whether left comes first or is the same.</returns>
    public static bool operator <=(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) <= 0;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The left position.</param>
    /// <param name="right">The right position.</param>
    /// <returns>Whether left comes after or is the same.</returns>
    public static bool operator >=(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Gets the position of an episode, or null for specials and episodes without numbers.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The position.</returns>
    public static EpisodePosition? From(Episode? episode)
        => episode is { ParentIndexNumber: > 0 and var season, IndexNumber: { } number }
            ? new EpisodePosition(season, number)
            : null;

    /// <inheritdoc />
    public int CompareTo(EpisodePosition other)
    {
        var season = Season.CompareTo(other.Season);
        return season != 0 ? season : Episode.CompareTo(other.Episode);
    }
}
