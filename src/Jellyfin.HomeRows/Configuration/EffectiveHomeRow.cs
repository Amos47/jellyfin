namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// A row of an effective layout.
/// </summary>
/// <param name="Row">The row.</param>
/// <param name="IsAvailable">Whether the row's provider is registered.</param>
/// <param name="SubRowKey">
/// The <see cref="HomeSubRow.Key"/> of the row of an expanding row this is; <c>null</c> for any other row.
/// </param>
public sealed record EffectiveHomeRow(HomeRowInstance Row, bool IsAvailable, string? SubRowKey = null);
