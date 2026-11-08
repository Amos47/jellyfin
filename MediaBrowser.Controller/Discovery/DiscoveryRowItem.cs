using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one film of a discovery row, as a user sees it.
/// </summary>
/// <param name="Film">The film in the list.</param>
/// <param name="Item">The user's library film if they own it, else the virtual discovery film.</param>
public record DiscoveryRowItem(DiscoveryFilmRef Film, BaseItem Item);
