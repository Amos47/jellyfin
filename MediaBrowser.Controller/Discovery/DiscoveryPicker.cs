using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: picks which discovery lists a user sees on a given day. Pure and deterministic: the same inputs give the same
/// result in any process, on any restart.
/// </summary>
public static class DiscoveryPicker
{
    /// <summary>
    /// Picks up to <paramref name="maxRows"/> list ids, as <see cref="Pick(Guid, DateOnly, IEnumerable{DiscoveryList}, int, string?)"/>
    /// does for lists without a group and no salt.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="day">The server's local date, worked out by the caller.</param>
    /// <param name="eligibleListIds">The lists that can be shown.</param>
    /// <param name="maxRows">The most lists to return; zero or negative returns none.</param>
    /// <returns>The picked list ids, in display order.</returns>
    public static IReadOnlyList<string> Pick(Guid userId, DateOnly day, IEnumerable<string> eligibleListIds, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(eligibleListIds);

        if (maxRows <= 0)
        {
            return Array.Empty<string>();
        }

        return Rank(userId, day, eligibleListIds.Distinct(StringComparer.Ordinal), id => id, null)
            .Take(maxRows)
            .ToList();
    }

    /// <summary>
    /// Picks up to <paramref name="maxRows"/> lists. Each list is ranked by the SHA-256 hash of the UTF-8 text
    /// <c>{userId:N}|{day:yyyy-MM-dd}|{listId}</c> (invariant culture, <c>N</c> = 32 lowercase hex digits), with
    /// <c>|{salt}</c> appended when <paramref name="salt"/> is not empty. Hashes are compared byte by byte; equal
    /// hashes fall back to ordinal list id order. The lists are then taken in that order, skipping a list whose
    /// <see cref="DiscoveryList.Group"/> (compared ordinally) was already taken; a list with no group (null or blank)
    /// is never skipped. So with fewer groups than <paramref name="maxRows"/>, fewer lists are returned. Duplicate ids
    /// count once (the first is kept), and the order of <paramref name="lists"/> does not matter.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="day">The server's local date, worked out by the caller.</param>
    /// <param name="lists">The lists that can be shown.</param>
    /// <param name="maxRows">The most lists to return; zero or negative returns none.</param>
    /// <param name="salt">The day's re-pick salt, or null or empty for none.</param>
    /// <returns>The picked lists, in display order.</returns>
    public static IReadOnlyList<DiscoveryList> Pick(Guid userId, DateOnly day, IEnumerable<DiscoveryList> lists, int maxRows, string? salt)
    {
        ArgumentNullException.ThrowIfNull(lists);

        if (maxRows <= 0)
        {
            return Array.Empty<DiscoveryList>();
        }

        var unique = lists.DistinctBy(l => l.Id, StringComparer.Ordinal);
        var picked = new List<DiscoveryList>(maxRows);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in Rank(userId, day, unique, l => l.Id, salt))
        {
            if (!string.IsNullOrWhiteSpace(list.Group) && !groups.Add(list.Group))
            {
                continue;
            }

            picked.Add(list);
            if (picked.Count == maxRows)
            {
                break;
            }
        }

        return picked;
    }

    private static IEnumerable<T> Rank<T>(Guid userId, DateOnly day, IEnumerable<T> items, Func<T, string> getId, string? salt)
        => items
            .Select(item => (Item: item, Id: getId(item), Hash: Hash(userId, day, getId(item), salt)))
            .OrderBy(x => x.Hash, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Item);

    private static string Hash(Guid userId, DateOnly day, string listId, string? salt)
    {
        var text = string.IsNullOrEmpty(salt)
            ? string.Create(CultureInfo.InvariantCulture, $"{userId:N}|{day:yyyy-MM-dd}|{listId}")
            : string.Create(CultureInfo.InvariantCulture, $"{userId:N}|{day:yyyy-MM-dd}|{listId}|{salt}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
