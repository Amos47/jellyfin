using System;

namespace MediaBrowser.Controller.TV;

/// <summary>
/// Fork: keeps one rewatch marker per user and series.
/// </summary>
public interface IRewatchMarkerStore
{
    /// <summary>
    /// Gets a user's marker for a series.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="seriesKey">The series presentation unique key.</param>
    /// <returns>The marker, or null.</returns>
    RewatchMarker? Get(Guid userId, string seriesKey);

    /// <summary>
    /// Saves a marker, replacing the user's marker for that series.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="marker">The marker.</param>
    void Set(Guid userId, RewatchMarker marker);

    /// <summary>
    /// Removes a user's marker for a series.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="seriesKey">The series presentation unique key.</param>
    void Remove(Guid userId, string seriesKey);
}
