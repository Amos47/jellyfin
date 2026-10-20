// Port of combineContinueWatching from jellyfin-androidtv CustomHomeRows.kt.
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.HomeRows.Providers.ContinueWatching;

/// <summary>
/// Combines in-progress items, next up episodes and recent activity into one Continue Watching list.
/// Next up does not include when a series was last watched, so that is taken from the recently played episodes.
/// </summary>
public static class ContinueWatchingCombiner
{
    /// <summary>
    /// Combines the three lists. Each series appears once, the list is ordered by most recent activity and entries
    /// without a known date keep their input order after the dated ones.
    /// </summary>
    /// <param name="resume">The in-progress items, most recently played first.</param>
    /// <param name="nextUp">The next up episodes in server order.</param>
    /// <param name="recentlyPlayed">The recently played episodes.</param>
    /// <returns>The combined items.</returns>
    public static IReadOnlyList<BaseItem> Combine(
        IReadOnlyList<ContinueWatchingCandidate> resume,
        IReadOnlyList<ContinueWatchingCandidate> nextUp,
        IReadOnlyList<ContinueWatchingCandidate> recentlyPlayed)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(nextUp);
        ArgumentNullException.ThrowIfNull(recentlyPlayed);

        var seriesActivity = new Dictionary<Guid, DateTime>();
        foreach (var candidate in recentlyPlayed)
        {
            if (candidate.SeriesId is not { } seriesId || candidate.LastPlayed is not { } lastPlayed)
            {
                continue;
            }

            if (!seriesActivity.TryGetValue(seriesId, out var existing) || lastPlayed > existing)
            {
                seriesActivity[seriesId] = lastPlayed;
            }
        }

        // The first item per series wins: resume is ordered by last played and next up by series activity.
        // The list keeps first-seen order, like Kotlin's LinkedHashMap.
        var nextUpBySeries = new Dictionary<Guid, ContinueWatchingCandidate>();
        var nextUpSeriesOrder = new List<Guid>();
        foreach (var candidate in nextUp)
        {
            if (candidate.SeriesId is { } seriesId && nextUpBySeries.TryAdd(seriesId, candidate))
            {
                nextUpSeriesOrder.Add(seriesId);
            }
        }

        var entries = new List<(BaseItem Item, DateTime? Date)>();
        var handledSeries = new HashSet<Guid>();

        foreach (var candidate in resume)
        {
            if (candidate.SeriesId is not { } seriesId)
            {
                entries.Add((candidate.Item, candidate.LastPlayed));
                continue;
            }

            if (!handledSeries.Add(seriesId))
            {
                continue;
            }

            var resumeDate = candidate.LastPlayed;
            DateTime? playedDate = seriesActivity.TryGetValue(seriesId, out var activity) ? activity : null;

            if (nextUpBySeries.TryGetValue(seriesId, out var nextUpCandidate)
                && resumeDate is { } rd
                && playedDate is { } pd
                && pd > rd)
            {
                // The in-progress episode is stale: a later episode was played afterwards
                entries.Add((nextUpCandidate.Item, pd));
            }
            else
            {
                entries.Add((candidate.Item, resumeDate ?? playedDate));
            }
        }

        foreach (var seriesId in nextUpSeriesOrder)
        {
            if (handledSeries.Add(seriesId))
            {
                entries.Add((nextUpBySeries[seriesId].Item, seriesActivity.TryGetValue(seriesId, out var activity) ? activity : null));
            }
        }

        // Kotlin's compareByDescending(nullsFirst()) reverses "nulls first, then ascending", which puts nulls last
        // and dated entries newest first. LINQ OrderBy is stable, so undated entries keep their order.
        return entries
            .OrderBy(e => e.Date is null)
            .ThenByDescending(e => e.Date)
            .Select(e => e.Item)
            .ToList();
    }
}
