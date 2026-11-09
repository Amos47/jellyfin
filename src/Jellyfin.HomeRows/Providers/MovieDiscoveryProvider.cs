using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using Microsoft.Extensions.Logging;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: Movie discovery. Expands into one row per Movie discovery list picked for the user today (see
/// <see cref="DiscoveryRowProvider"/>); Series lists are left to <see cref="ShowDiscoveryProvider"/>.
/// </summary>
public sealed class MovieDiscoveryProvider : DiscoveryRowProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MovieDiscoveryProvider"/> class.
    /// </summary>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="store">The discovery store, for the lists and the shown log.</param>
    /// <param name="adapter">The item adapter.</param>
    /// <param name="logger">The logger.</param>
    public MovieDiscoveryProvider(IDiscoveryRowPlanner planner, IDiscoveryStore store, IHomeRowItemAdapter adapter, ILogger<MovieDiscoveryProvider> logger)
        : base(planner, store, adapter, logger)
    {
    }

    /// <inheritdoc />
    public override string Id => HomeRowProviderIds.MovieDiscovery;

    /// <inheritdoc />
    public override string Name => "Movie discovery";

    /// <inheritdoc />
    public override string Description => "Films you don't own, from lists picked for you each day.";

    /// <inheritdoc />
    public override DiscoveryMediaType MediaType => DiscoveryMediaType.Movie;

    /// <inheritdoc />
    protected override string TitlesNoun => "films";

    // The Movie overloads, as before shows.

    /// <inheritdoc />
    protected override Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, CancellationToken cancellationToken)
        => Planner.GetPickedListsAsync(user, maxRows, rules, day, cancellationToken);

    /// <inheritdoc />
    protected override Task MarkShownAsync(IEnumerable<int> tmdbIds, DateOnly day, CancellationToken cancellationToken)
        => Store.MarkShownAsync(tmdbIds, day, cancellationToken);
}
