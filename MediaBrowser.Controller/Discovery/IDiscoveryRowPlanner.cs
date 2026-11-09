using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: works out which discovery lists a user sees today and which titles each shows. The one place that decides
/// this, so the Movie and Show discovery rows, the picks API and the trailer queue always agree. A list's titles
/// resolve by their own type: a show never to a film with the same TMDB id.
/// </summary>
public interface IDiscoveryRowPlanner
{
    /// <summary>
    /// Gets the lists picked for a user on a day, in display order. <see cref="DiscoveryPicker"/> picks among the
    /// eligible lists, one list per group, with the day's re-pick salt from <see cref="IDiscoveryStore.GetPickSaltAsync"/>.
    /// <list type="bullet">
    /// <item>With <see cref="DiscoveryPickRules.RequireTrailer"/> on, a list is eligible when it has at least
    /// <see cref="DiscoveryPickRules.MinReadyFilms"/> films playable for the user (see <see cref="CountReadyFilms"/>).
    /// The user's first full pick of the day (one that fills <paramref name="maxRows"/>, or, when the stored lists can
    /// give fewer rows, one per group plus one per ungrouped list, holds all of them) is saved in full
    /// (<see cref="IDiscoveryStore.AddPickSnapshotAsync(Guid, DateOnly, string?, IReadOnlyList{string}, CancellationToken)"/>) and read back for the rest of the day, so lists becoming
    /// ready (or unready) later don't change it; a smaller <paramref name="maxRows"/> takes a prefix, and saved lists
    /// that were deleted since are left out. A saved pick whose lists were all deleted is dropped and picked again. A
    /// smaller pick is not saved and is worked out again on later reads, through a readiness cache kept per user, day
    /// and salt for <c>60</c> seconds (see <see cref="InvalidateReadiness"/>). A re-pick clears the saved picks of its
    /// day.</item>
    /// <item>With it off, a list is eligible when it has at least one film the user may see (an owned library film
    /// counts as visible), and nothing is saved.</item>
    /// </list>
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="maxRows">The most lists to return.</param>
    /// <param name="rules">The row's readiness rules.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picked lists.</returns>
    Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the lists of a type picked for a user on a day, in display order, as
    /// <see cref="GetPickedListsAsync(User, int, DiscoveryPickRules, DateOnly, CancellationToken)"/> (which is the
    /// <see cref="DiscoveryMediaType.Movie"/> pick) picks them, among the lists of that <see cref="DiscoveryList.ItemType"/>
    /// only: Movie discovery rows pick films, Show discovery rows shows. Each type has its own saved picks of the day
    /// and its own readiness; the day's salt (and so a re-pick) is shared.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="maxRows">The most lists to return.</param>
    /// <param name="rules">The row's readiness rules.</param>
    /// <param name="day">The server's local date.</param>
    /// <param name="type">The media type of the lists.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picked lists.</returns>
    Task<IReadOnlyList<DiscoveryList>> GetPickedListsAsync(User user, int maxRows, DiscoveryPickRules rules, DateOnly day, DiscoveryMediaType type, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the films of a list as the user sees them, in list order: the user's library film when they own it,
    /// else the virtual discovery film if the user may see it. Films with neither are left out.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="list">The list.</param>
    /// <returns>The row items.</returns>
    IReadOnlyList<DiscoveryRowItem> GetRowItems(User user, DiscoveryList list);

    /// <summary>
    /// Gets the films of a list as the user sees them, as <see cref="GetRowItems(User, DiscoveryList)"/> does; with
    /// <paramref name="requireTrailer"/>, only the playable ones: owned films, and virtual films with a local trailer.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="list">The list.</param>
    /// <param name="requireTrailer">Whether to keep only playable films.</param>
    /// <returns>The row items.</returns>
    IReadOnlyList<DiscoveryRowItem> GetRowItems(User user, DiscoveryList list, bool requireTrailer);

    /// <summary>
    /// Counts the films of a list that are playable for the user: films they own (visible through their library
    /// query), plus virtual discovery films they may see that have at least one local trailer.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="list">The list.</param>
    /// <returns>The number of playable films.</returns>
    int CountReadyFilms(User user, DiscoveryList list);

    /// <summary>
    /// Gets whether one film is playable for the user, as <see cref="CountReadyFilms"/> counts it.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="film">The film.</param>
    /// <returns>Whether the film is playable.</returns>
    bool IsPlayable(User user, DiscoveryFilmRef film);

    /// <summary>
    /// Drops the cached readiness of every user, so the next read counts again. Called on a re-pick and when a list
    /// or a trailer changes.
    /// </summary>
    void InvalidateReadiness();
}
