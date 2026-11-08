namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// A row of an effective layout.
/// </summary>
/// <param name="Row">The row.</param>
/// <param name="IsAvailable">Whether the row's provider is registered.</param>
/// <param name="SubRow">Which of the rows of an expanding row this is, from 0; 0 for any other row.</param>
public sealed record EffectiveHomeRow(HomeRowInstance Row, bool IsAvailable, int SubRow = 0);
