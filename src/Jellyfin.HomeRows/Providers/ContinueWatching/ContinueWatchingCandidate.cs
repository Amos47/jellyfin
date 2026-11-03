using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.HomeRows.Providers.ContinueWatching;

/// <summary>
/// An item considered for the Continue Watching row.
/// </summary>
/// <param name="Item">The item.</param>
/// <param name="SeriesId">The series id for episodes, otherwise null.</param>
/// <param name="LastPlayed">The last played date of the user, if any.</param>
public sealed record ContinueWatchingCandidate(BaseItem Item, Guid? SeriesId, DateTime? LastPlayed);
