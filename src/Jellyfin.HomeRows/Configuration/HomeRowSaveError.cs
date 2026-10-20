namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// An error that rejects saving a layout.
/// </summary>
/// <param name="RowIndex">The index of the row in the saved list, or -1 for an error about the whole layout.</param>
/// <param name="Key">"provider", "title", "id", "rows" or a setting key.</param>
/// <param name="Message">The error message.</param>
public sealed record HomeRowSaveError(int RowIndex, string Key, string Message);
