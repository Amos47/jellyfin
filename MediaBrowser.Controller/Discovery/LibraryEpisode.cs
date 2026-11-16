using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: an episode that is on the server, as the show page reads it from the library.
/// </summary>
/// <param name="ItemId">The episode's item id.</param>
/// <param name="SeasonNumber">The season number (the episode's parent index number).</param>
/// <param name="EpisodeNumber">The episode number, or null when the library has none.</param>
/// <param name="EpisodeNumberEnd">The last episode number of a multi-episode file, or null.</param>
/// <param name="Name">The episode's name.</param>
/// <param name="Overview">The episode's overview.</param>
/// <param name="AirDate">The episode's premiere date.</param>
/// <param name="RuntimeMinutes">The run time in minutes.</param>
/// <param name="SeasonName">The name of the episode's season in the library.</param>
public record LibraryEpisode(
    Guid ItemId,
    int SeasonNumber,
    int? EpisodeNumber,
    int? EpisodeNumberEnd,
    string? Name,
    string? Overview,
    DateOnly? AirDate,
    int? RuntimeMinutes,
    string? SeasonName);
