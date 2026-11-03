namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// A row of an effective layout.
/// </summary>
/// <param name="Row">The row.</param>
/// <param name="IsAvailable">Whether the row's provider is registered.</param>
public sealed record EffectiveHomeRow(HomeRowInstance Row, bool IsAvailable);
