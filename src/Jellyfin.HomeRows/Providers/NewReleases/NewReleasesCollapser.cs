using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.HomeRows.Providers.NewReleases;

/// <summary>
/// Collapses new releases to one entry per series.
/// </summary>
public static class NewReleasesCollapser
{
    /// <summary>
    /// Collapses new releases to one entry per series, keeping movies as-is. A series shows its earliest unwatched
    /// new episode so it can be watched in order, and is positioned by its newest release.
    /// </summary>
    /// <param name="newestFirst">The items, ordered by release date, newest first.</param>
    /// <returns>The collapsed items.</returns>
    // Port of collapseNewReleases from jellyfin-androidtv CustomHomeRows.kt.
    public static IReadOnlyList<BaseItem> Collapse(IReadOnlyList<BaseItem> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);

        var groups = new Dictionary<Guid, List<BaseItem>>();
        var order = new List<Guid>();
        foreach (var item in newestFirst)
        {
            var key = item is Episode { SeriesId: var seriesId } && !seriesId.Equals(Guid.Empty) ? seriesId : item.Id;
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add(key);
            }

            group.Add(item);
        }

        return order.Select(key => groups[key].MinBy(Order, EpisodeOrderComparer.Instance)!).ToList();
    }

    private static (DateTime? Premiere, int? Season, int? Number) Order(BaseItem item)
        => (item.PremiereDate, item.ParentIndexNumber, item.IndexNumber);

    private sealed class EpisodeOrderComparer : IComparer<(DateTime? Premiere, int? Season, int? Number)>
    {
        public static readonly EpisodeOrderComparer Instance = new();

        public int Compare((DateTime? Premiere, int? Season, int? Number) x, (DateTime? Premiere, int? Season, int? Number) y)
        {
            var c = Comparer<DateTime?>.Default.Compare(x.Premiere, y.Premiere);
            if (c != 0)
            {
                return c;
            }

            c = Comparer<int?>.Default.Compare(x.Season, y.Season);
            return c != 0 ? c : Comparer<int?>.Default.Compare(x.Number, y.Number);
        }
    }
}
