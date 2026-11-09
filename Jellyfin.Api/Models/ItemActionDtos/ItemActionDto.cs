using System.Collections.Generic;
using MediaBrowser.Controller.Discovery;

namespace Jellyfin.Api.Models.ItemActionDtos;

/// <summary>
/// An action an app can offer on an item's page.
/// </summary>
/// <param name="Id">The action id.</param>
/// <param name="Label">The button label; for an action with a state, the label of the current state.</param>
/// <param name="Icon">The icon name.</param>
/// <param name="Kind">
/// What the app does with it: <c>play-queue</c> fetches the action's queue and plays it; <c>toggle</c> posts the action
/// (<c>POST Items/{itemId}/Actions/{actionId}</c>), which flips its <see cref="State"/> and answers the updated action.
/// Apps ignore kinds they don't know.
/// </param>
public sealed record ItemActionDto(string Id, string Label, string Icon, string Kind)
{
    /// <summary>
    /// Gets the current state (a toggle: <c>on</c> or <c>off</c>), or null for an action without one.
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

    /// <summary>
    /// Gets the DTO of an action.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>The DTO.</returns>
    public static ItemActionDto From(ItemAction action)
    {
        System.ArgumentNullException.ThrowIfNull(action);

        return new ItemActionDto(action.Id, action.Label, action.Icon, action.Kind)
        {
            State = action.State,
            Labels = action.Labels,
            Enabled = action.Enabled,
        };
    }
}
