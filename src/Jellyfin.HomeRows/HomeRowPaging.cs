namespace Jellyfin.HomeRows;

/// <summary>
/// Paging parameters of a home row request.
/// </summary>
/// <param name="StartIndex">The index of the first item.</param>
/// <param name="Limit">The maximum number of items.</param>
public sealed record HomeRowPaging(int StartIndex, int Limit);
