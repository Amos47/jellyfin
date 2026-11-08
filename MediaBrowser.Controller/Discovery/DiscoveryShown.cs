using System;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: when a discovery film was shown on the home screen.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="FirstShown">The first day it was shown.</param>
/// <param name="LastShown">The last day it was shown.</param>
public record DiscoveryShown(int TmdbId, DateOnly FirstShown, DateOnly LastShown);
