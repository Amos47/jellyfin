using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: a list of films pushed to the discovery hub.
/// </summary>
/// <param name="Id">The id of the list.</param>
/// <param name="Title">The title of the list.</param>
/// <param name="Source">Where the list came from.</param>
/// <param name="Films">The films, in order.</param>
public record DiscoveryList(string Id, string Title, string Source, IReadOnlyList<DiscoveryFilmRef> Films);
