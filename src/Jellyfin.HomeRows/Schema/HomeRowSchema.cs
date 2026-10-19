using System.Collections.Generic;

namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// The settings schema of a home row provider.
/// </summary>
/// <param name="Settings">The settings.</param>
public sealed record HomeRowSchema(IReadOnlyList<HomeRowSetting> Settings);
