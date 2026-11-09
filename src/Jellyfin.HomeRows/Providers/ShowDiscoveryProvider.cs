using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using Microsoft.Extensions.Logging;

namespace Jellyfin.HomeRows.Providers;

/// <summary>
/// Fork: Show discovery. Expands into one row per Series discovery list picked for the user today, with the same
/// settings as Movie discovery (see <see cref="DiscoveryRowProvider"/>). Its picks and shown log are the shows' own;
/// the day's re-pick salt is shared. Not in the default layout: the owner turns it on.
/// </summary>
public sealed class ShowDiscoveryProvider : DiscoveryRowProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShowDiscoveryProvider"/> class.
    /// </summary>
    /// <param name="planner">The discovery row planner.</param>
    /// <param name="store">The discovery store, for the lists and the shown log.</param>
    /// <param name="adapter">The item adapter.</param>
    /// <param name="logger">The logger.</param>
    public ShowDiscoveryProvider(IDiscoveryRowPlanner planner, IDiscoveryStore store, IHomeRowItemAdapter adapter, ILogger<ShowDiscoveryProvider> logger)
        : base(planner, store, adapter, logger)
    {
    }

    /// <inheritdoc />
    public override string Id => HomeRowProviderIds.ShowDiscovery;

    /// <inheritdoc />
    public override string Name => "Show discovery";

    /// <inheritdoc />
    public override string Description => "Shows you don't own, from lists picked for you each day.";

    /// <inheritdoc />
    public override DiscoveryMediaType MediaType => DiscoveryMediaType.Series;

    /// <inheritdoc />
    protected override string TitlesNoun => "shows";

    /// <inheritdoc />
    protected override Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, CancellationToken cancellationToken)
        => Planner.GetPickedListsAsync(user, maxRows, rules, day, DiscoveryMediaType.Series, cancellationToken);

    /// <inheritdoc />
    protected override Task MarkShownAsync(IEnumerable<int> tmdbIds, DateOnly day, CancellationToken cancellationToken)
        => Store.MarkShownAsync(tmdbIds, DiscoveryMediaType.Series, day, cancellationToken);
}
