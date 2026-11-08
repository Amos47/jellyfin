namespace Jellyfin.HomeRows;

/// <summary>
/// One of the rows an <see cref="IExpandingHomeRowProvider"/> row expands into.
/// </summary>
/// <param name="Key">
/// The provider's stable key for the row's content (Movie discovery: the list id). The derived row id is made from it,
/// and it comes back as <see cref="HomeRowContext.SubRowKey"/> when the row's items are asked for.
/// </param>
public sealed record HomeSubRow(string Key);
