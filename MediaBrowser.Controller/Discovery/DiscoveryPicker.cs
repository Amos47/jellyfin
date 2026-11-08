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
    /// Picks up to <paramref name="maxRows"/> lists. Each list id is ranked by the SHA-256 hash of the UTF-8 text
    /// <c>{userId:N}|{day:yyyy-MM-dd}|{listId}</c> (invariant culture, <c>N</c> = 32 lowercase hex digits). Hashes are
    /// compared byte by byte; equal hashes fall back to ordinal list id order. Duplicate ids count once, and the order
    /// of <paramref name="eligibleListIds"/> does not matter.
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

        return eligibleListIds
            .Distinct(StringComparer.Ordinal)
            .Select(id => (Id: id, Hash: Hash(userId, day, id)))
            .OrderBy(x => x.Hash, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(maxRows)
            .Select(x => x.Id)
            .ToList();
    }

    private static string Hash(Guid userId, DateOnly day, string listId)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{userId:N}|{day:yyyy-MM-dd}|{listId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
