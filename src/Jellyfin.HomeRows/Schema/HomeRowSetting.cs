using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Jellyfin.HomeRows.Schema;

/// <summary>
/// A single setting of a home row provider.
/// </summary>
/// <param name="Key">The setting key.</param>
/// <param name="Type">The setting type.</param>
/// <param name="Title">The display title.</param>
public sealed record HomeRowSetting(string Key, HomeRowSettingType Type, string Title)
{
    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the default value. A setting without a default is required.
    /// </summary>
    public JsonNode? Default { get; init; }

    /// <summary>
    /// Gets the minimum of an integer setting.
    /// </summary>
    public int? Minimum { get; init; }

    /// <summary>
    /// Gets the maximum of an integer setting.
    /// </summary>
    public int? Maximum { get; init; }

    /// <summary>
    /// Gets the minimum number of selected values of a multi-enum setting.
    /// </summary>
    public int? MinItems { get; init; }

    /// <summary>
    /// Gets the options of an enum or multi-enum setting.
    /// </summary>
    public IReadOnlyList<HomeRowOption> Options { get; init; } = [];

    /// <summary>
    /// Gets the optional UI hint.
    /// </summary>
    public string? UiHint { get; init; }
}
