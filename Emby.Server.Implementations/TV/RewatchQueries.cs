using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.TV;

/// <summary>
/// Fork: the episode queries the rewatch marker uses. Specials are left out, as in Next Up.
/// </summary>
internal static class RewatchQueries
{
    /// <summary>
    /// Gets the query for the highest-numbered played episode in a series, the frontier.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="seriesKey">The series presentation unique key.</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery Frontier(User user, string seriesKey)
    {
        var query = Episodes(user, seriesKey, SortOrder.Descending);
        query.IsPlayed = true;
        query.Limit = 1;
        return query;
    }

    /// <summary>
    /// Gets the query for a series' episodes.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="seriesKey">The series presentation unique key.</param>
    /// <param name="order">Season and episode order.</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery Episodes(User user, string seriesKey, SortOrder order = SortOrder.Ascending)
        => new(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SeriesPresentationUniqueKey = seriesKey,
            ParentIndexNumberNotEquals = 0,
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = [(ItemSortBy.ParentIndexNumber, order), (ItemSortBy.IndexNumber, order)]
        };
}
