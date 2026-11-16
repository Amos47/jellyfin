using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: one episode of a show as TMDB lists it.
/// </summary>
/// <param name="EpisodeNumber">The episode number.</param>
/// <param name="Name">The episode's name.</param>
/// <param name="Overview">The episode's overview.</param>
/// <param name="AirDate">The air date, or null when TMDB has none yet.</param>
/// <param name="RuntimeMinutes">The run time in minutes.</param>
/// <param name="StillPath">TMDB's still path (such as <c>/abc.jpg</c>), or null.</param>
public record TmdbEpisode(int EpisodeNumber, string? Name, string? Overview, DateOnly? AirDate, int? RuntimeMinutes, string? StillPath);
