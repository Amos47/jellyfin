namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// An option of an enum setting.
/// </summary>
/// <param name="Value">The stored value.</param>
/// <param name="Label">The display label.</param>
public sealed record HomeRowOption(string Value, string Label);
