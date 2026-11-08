using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a list of films pushed to the discovery hub.
/// </summary>
/// <param name="Id">The id of the list.</param>
/// <param name="Title">The title of the list.</param>
/// <param name="Source">Where the list came from.</param>
/// <param name="Films">The films, in order.</param>
/// <param name="Group">
/// The group of the list, or null. A user's rows show at most one list of a group a day; a list without a group is a
/// group of its own.
/// </param>
public record DiscoveryList(string Id, string Title, string Source, IReadOnlyList<DiscoveryFilmRef> Films, string? Group = null);
