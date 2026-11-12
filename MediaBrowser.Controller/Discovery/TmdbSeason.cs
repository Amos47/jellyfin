using System.Collections.Generic;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one season of a show as TMDB lists it.
/// </summary>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="Name">The season's name.</param>
/// <param name="Episodes">The season's episodes.</param>
public record TmdbSeason(int SeasonNumber, string? Name, IReadOnlyList<TmdbEpisode> Episodes);
