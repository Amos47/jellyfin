using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one item of an action's play queue, with the names a player shows for it.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Title">The title to show, or null to use the item's own name.</param>
/// <param name="Subtitle">The line under the title, or null.</param>
/// <param name="FilmId">
/// The film the entry belongs to (for a trailer: the user's owned film when they own it, else the discovery copy), or
/// null when it isn't known. A player closes onto this film's page.
/// </param>
public record QueueEntry(Guid Id, string? Title, string? Subtitle, Guid? FilmId = null);
