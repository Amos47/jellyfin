namespace Jellyfin.Api.Models.ItemActionDtos;

/// <summary>
/// An action an app can offer on an item's page.
/// </summary>
/// <param name="Id">The action id.</param>
/// <param name="Label">The button label.</param>
/// <param name="Icon">The icon name.</param>
/// <param name="Kind">What the app does with it: <c>play-queue</c> fetches the action's queue and plays it.</param>
public sealed record ItemActionDto(string Id, string Label, string Icon, string Kind);
