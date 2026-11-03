using System.Collections.Generic;

namespace Jellyfin.HomeRows.Configuration;

/// <summary>
/// The home row layout that applies to a user.
/// </summary>
/// <param name="Source">Where the layout comes from.</param>
/// <param name="Rows">The rows, in display order.</param>
public sealed record EffectiveHomeRowLayout(HomeRowLayoutSource Source, IReadOnlyList<EffectiveHomeRow> Rows);
