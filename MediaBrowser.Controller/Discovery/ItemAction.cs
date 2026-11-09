using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: an action an app can offer on an item's page, such as Watch trailer.
/// </summary>
/// <param name="Id">The action id, unique among the action providers.</param>
/// <param name="Label">The button label (for an action with a <see cref="State"/>, the label of that state).</param>
/// <param name="Icon">The icon name the apps map to a symbol.</param>
/// <param name="Kind">
/// What the app does with it: <see cref="PlayQueueKind"/> fetches the queue and plays it; <see cref="ToggleKind"/>
/// posts the action, which flips its state and answers the updated action. Apps ignore kinds they don't know.
/// </param>
public record ItemAction(string Id, string Label, string Icon, string Kind)
{
    /// <summary>
    /// The kind of an action whose queue the app plays in order.
    /// </summary>
    public const string PlayQueueKind = "play-queue";

    /// <summary>
    /// The kind of an action that is on or off (<see cref="StateOn"/>, <see cref="StateOff"/>); posting it flips it.
    /// </summary>
    public const string ToggleKind = "toggle";

    /// <summary>
    /// The state of a toggle that is on.
    /// </summary>
    public const string StateOn = "on";

    /// <summary>
    /// The state of a toggle that is off.
    /// </summary>
    public const string StateOff = "off";

    /// <summary>
    /// Gets the current state, or null for an action without one.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Gets the label of each state, by state, or null for an action without states.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// Gets whether the action can be used now, or null when it always can.
    /// </summary>
    public bool? Enabled { get; init; }
}
