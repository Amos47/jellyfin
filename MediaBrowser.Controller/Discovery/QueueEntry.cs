using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one item of an action's play queue, with the names a player shows for it.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Title">The title to show, or null to use the item's own name.</param>
/// <param name="Subtitle">The line under the title, or null.</param>
public record QueueEntry(Guid Id, string? Title, string? Subtitle);
