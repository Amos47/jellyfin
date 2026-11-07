using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.TV;

/// <summary>
/// Fork: finds the episode a series' rewatch marker offers next, for Next Up.
/// </summary>
public interface IRewatchCandidateFinder
{
    /// <summary>
    /// Gets the rewatch episode to offer: the marker's episode while it's partly watched, otherwise the one after it.
    /// Deletes the marker when the rewatch has caught up with the frontier or its episode is gone.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="seriesKey">The series presentation unique key.</param>
    /// <param name="frontier">The highest-numbered played episode in the series.</param>
    /// <param name="options">The DTO options for the episode query.</param>
    /// <returns>The episode and when the marker was set, or null.</returns>
    (Episode Episode, DateTime Date)? GetCandidate(User user, string seriesKey, BaseItem? frontier, DtoOptions options);
}
