using System;

namespace MediaBrowser.Controller.TV;

/// <summary>
/// Fork: the episode a user last rewatched in a series. Next Up offers what comes after it once there's nothing new.
/// </summary>
/// <param name="SeriesKey">The series presentation unique key, as Next Up uses.</param>
/// <param name="EpisodeId">The primary version id of the rewatched episode.</param>
/// <param name="Date">When it was rewatched.</param>
public sealed record RewatchMarker(string SeriesKey, Guid EpisodeId, DateTime Date);
