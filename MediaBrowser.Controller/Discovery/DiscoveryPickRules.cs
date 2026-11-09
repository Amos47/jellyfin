namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: the readiness rules of a Movie discovery row. A film is playable for a user when they own it (it is visible
/// through their library query), or when it is a virtual discovery film they may see with at least one local trailer.
/// </summary>
/// <param name="RequireTrailer">
/// Whether rows show only playable films and only ready lists are picked, the day's picks being saved. When false,
/// every film the user may see is shown and every list with one is eligible, picked afresh on each read.
/// </param>
/// <param name="MinReadyFilms">
/// The fewest playable films a list needs to be picked while <paramref name="RequireTrailer"/> is on.
/// </param>
public sealed record DiscoveryPickRules(bool RequireTrailer, int MinReadyFilms)
{
    /// <summary>
    /// The default of <see cref="MinReadyFilms"/>: a list is ready with this many playable films.
    /// </summary>
    public const int DefaultMinReadyFilms = 5;

    /// <summary>
    /// The smallest allowed <see cref="MinReadyFilms"/>.
    /// </summary>
    public const int MinimumMinReadyFilms = 1;

    /// <summary>
    /// The largest allowed <see cref="MinReadyFilms"/>.
    /// </summary>
    public const int MaximumMinReadyFilms = 20;

    /// <summary>
    /// Gets the default rules: trailers required, ready with <see cref="DefaultMinReadyFilms"/> playable films.
    /// </summary>
    public static DiscoveryPickRules Default { get; } = new(true, DefaultMinReadyFilms);

    /// <summary>
    /// Gets the rules with trailers not required: every visible film and every list with one.
    /// </summary>
    public static DiscoveryPickRules AllVisible { get; } = new(false, DefaultMinReadyFilms);
}
