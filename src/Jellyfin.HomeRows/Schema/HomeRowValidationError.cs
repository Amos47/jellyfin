namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// A validation error of a single setting.
/// </summary>
/// <param name="Key">The setting key.</param>
/// <param name="Message">The error message.</param>
public sealed record HomeRowValidationError(string Key, string Message);
