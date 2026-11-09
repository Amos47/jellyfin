using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: creates a pathless virtual <see cref="Movie"/> in the discovery folder for every TMDB id in a Movie discovery
/// list, and a pathless virtual <see cref="Series"/> (no seasons or episodes) for every TMDB id in a Series list, and
/// deletes the virtual titles (with their extras) that are in no list of their type. A title in any user's list (such
/// as Saved), or with a download request that isn't done, is kept even when no discovery list has it any more, by its
/// own type. Library items are never touched.
/// </summary>
public sealed class DiscoveryFilmSync : IDiscoveryFilmSync, IDisposable
{
    private const string IdPrefix = "discovery-tmdb-";

    // A show's id key differs from a film's, so the two items of one TMDB id never share an id.
    private const string SeriesIdPrefix = "discovery-tmdb-tv-";

    private readonly ILibraryManager _libraryManager;
    private readonly IDiscoveryStore _store;
    private readonly IUserListStore _userLists;
    private readonly IDiscoveryRequestStore _requests;
    private readonly IDiscoveryFolderLocator _folderLocator;
    private readonly IDiscoveryMetadataRefresher _refresher;
    private readonly ILogger<DiscoveryFilmSync> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryFilmSync"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The discovery store.</param>
    /// <param name="userLists">The user list store, whose films are kept.</param>
    /// <param name="requests">The download request store, whose active requests' films are kept.</param>
    /// <param name="folderLocator">The discovery folder locator.</param>
    /// <param name="refresher">The metadata refresher.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryFilmSync(
        ILibraryManager libraryManager,
        IDiscoveryStore store,
        IUserListStore userLists,
        IDiscoveryRequestStore requests,
        IDiscoveryFolderLocator folderLocator,
        IDiscoveryMetadataRefresher refresher,
        ILogger<DiscoveryFilmSync> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _userLists = userLists;
        _requests = requests;
        _folderLocator = folderLocator;
        _refresher = refresher;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _syncLock.Dispose();

    /// <inheritdoc />
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        // One sync at a time, so two list changes at once can't both create (or delete) the same film.
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lists = await _store.GetListsAsync(cancellationToken).ConfigureAwait(false);

            // (TMDB id, type) -> IMDb id (the first one any list of that type gives). A list's films are all of its type.
            var wanted = new Dictionary<(int TmdbId, DiscoveryMediaType Type), string?>();
            foreach (var list in lists)
            {
                foreach (var film in list.Films)
                {
                    var key = (film.TmdbId, list.ItemType);
                    if (!wanted.TryGetValue(key, out var imdbId) || string.IsNullOrEmpty(imdbId))
                    {
                        wanted[key] = string.IsNullOrEmpty(film.ImdbId) ? null : film.ImdbId;
                    }
                }
            }

            // Titles in a user's list or with a request that isn't done are kept, though not created: they were
            // discovery titles when they were saved or requested. Read under the sync lock, which saves and requests take
            // too (RunExclusiveAsync), so a title saved or requested meanwhile is never deleted. Each by its own type.
            var wantedIds = wanted.Keys.Select(k => GetTitleId(k.TmdbId, k.Type)).ToHashSet();
            foreach (var type in Enum.GetValues<DiscoveryMediaType>())
            {
                var listed = type == DiscoveryMediaType.Movie
                    ? await _userLists.GetListedTmdbIdsAsync(cancellationToken).ConfigureAwait(false)
                    : await _userLists.GetListedTmdbIdsAsync(type, cancellationToken).ConfigureAwait(false);
                var requested = type == DiscoveryMediaType.Movie
                    ? await _requests.GetActiveTmdbIdsAsync(cancellationToken).ConfigureAwait(false)
                    : await _requests.GetActiveTmdbIdsAsync(type, cancellationToken).ConfigureAwait(false);
                wantedIds.UnionWith(listed.Concat(requested).Select(id => GetTitleId(id, type)));
            }

            var folder = _folderLocator.GetFolder();

            foreach (var title in GetTitles(folder.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!wantedIds.Contains(title.Id))
                {
                    DeleteTitle(title);
                }
            }

            foreach (var ((tmdbId, type), imdbId) in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = this.FindTitle(tmdbId, type);
                if (existing is null)
                {
                    CreateTitle(folder, tmdbId, type, imdbId);
                }
                else if (imdbId is not null
                    && !string.Equals(existing.GetProviderId(MetadataProvider.Imdb), imdbId, StringComparison.Ordinal))
                {
                    existing.SetProviderId(MetadataProvider.Imdb, imdbId);
                    await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <inheritdoc />
    public Movie? FindFilm(int tmdbId)
        => _libraryManager.GetItemById(GetTitleId(tmdbId, DiscoveryMediaType.Movie)) as Movie;

    /// <inheritdoc />
    public Movie? FindFilmByImdb(string imdbId)
        => FindByImdb(imdbId, DiscoveryMediaType.Movie) as Movie;

    /// <inheritdoc />
    public BaseItem? FindOwned(User user, int tmdbId, string? imdbId)
    {
        ArgumentNullException.ThrowIfNull(user);
        return QueryOwned(new InternalItemsQuery(user), tmdbId, imdbId, DiscoveryMediaType.Movie);
    }

    /// <inheritdoc />
    public BaseItem? FindOwned(int tmdbId, string? imdbId)
        => QueryOwned(new InternalItemsQuery(), tmdbId, imdbId, DiscoveryMediaType.Movie);

    /// <inheritdoc />
    public Series? FindSeries(int tmdbId)
        => _libraryManager.GetItemById(GetTitleId(tmdbId, DiscoveryMediaType.Series)) as Series;

    /// <inheritdoc />
    public Series? FindSeriesByImdb(string imdbId)
        => FindByImdb(imdbId, DiscoveryMediaType.Series) as Series;

    /// <inheritdoc />
    public BaseItem? FindOwnedSeries(User user, int tmdbId, string? imdbId)
    {
        ArgumentNullException.ThrowIfNull(user);
        return QueryOwned(new InternalItemsQuery(user), tmdbId, imdbId, DiscoveryMediaType.Series);
    }

    /// <inheritdoc />
    public BaseItem? FindOwnedSeries(int tmdbId, string? imdbId)
        => QueryOwned(new InternalItemsQuery(), tmdbId, imdbId, DiscoveryMediaType.Series);

    private BaseItem? FindByImdb(string imdbId, DiscoveryMediaType type)
    {
        var folderId = _folderLocator.FolderId;
        if (string.IsNullOrEmpty(imdbId) || folderId.Equals(Guid.Empty))
        {
            return null;
        }

        var titles = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeDiscovery = true,
            ParentId = folderId,
            IncludeItemTypes = [DiscoveryTitles.ToItemKind(type)],
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Imdb.ToString()] = imdbId },
            Limit = 1,
            DtoOptions = new DtoOptions(false),
        });

        // The light query's item lacks its extras: load the title in full.
        return titles.Count > 0 ? _libraryManager.GetItemById(titles[0].Id) : null;
    }

    private BaseItem? QueryOwned(InternalItemsQuery query, int tmdbId, string? imdbId, DiscoveryMediaType type)
    {
        var providerIds = new Dictionary<string, string>
        {
            [MetadataProvider.Tmdb.ToString()] = tmdbId.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(imdbId))
        {
            providerIds[MetadataProvider.Imdb.ToString()] = imdbId;
        }

        // The discovery guard (no IncludeDiscovery) keeps the virtual titles out, so only library items match: films for
        // a Movie, shows for a Series.
        query.IncludeItemTypes = [DiscoveryTitles.ToItemKind(type)];
        query.Recursive = true;
        query.IsVirtualItem = false;
        query.HasAnyProviderId = providerIds;
        query.Limit = 1;
        query.DtoOptions = new DtoOptions(false);
        var owned = _libraryManager.GetItemList(query);
        return owned.Count > 0 ? owned[0] : null;
    }

    private Guid GetTitleId(int tmdbId, DiscoveryMediaType type)
        => type == DiscoveryMediaType.Series
            ? _libraryManager.GetNewItemId(SeriesIdPrefix + tmdbId.ToString(CultureInfo.InvariantCulture), typeof(Series))
            : _libraryManager.GetNewItemId(IdPrefix + tmdbId.ToString(CultureInfo.InvariantCulture), typeof(Movie));

    private IReadOnlyList<BaseItem> GetTitles(Guid folderId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeDiscovery = true,
            ParentId = folderId,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
        });

    private void CreateTitle(BasePluginFolder folder, int tmdbId, DiscoveryMediaType type, string? imdbId)
    {
        // No path: pathless items are never removed by library scans. A show gets no seasons or episodes: the TMDB series
        // provider fills in the show itself (overview, images, remote trailers) and creates no children.
        BaseItem title = type == DiscoveryMediaType.Series ? new Series() : new Movie();
        title.Id = GetTitleId(tmdbId, type);
        title.Name = "TMDB " + tmdbId.ToString(CultureInfo.InvariantCulture);
        title.IsVirtualItem = true;
        title.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));
        if (imdbId is not null)
        {
            title.SetProviderId(MetadataProvider.Imdb, imdbId);
        }

        _logger.LogInformation("Creating discovery {Type} for TMDB id {TmdbId}", type == DiscoveryMediaType.Series ? "show" : "film", tmdbId);
        folder.AddChild(title);
        _refresher.QueueRefresh(title.Id);
    }

    private void DeleteTitle(BaseItem film)
    {
        // Only virtual pathless titles are ever deleted here, whatever else ends up in the folder.
        if (!film.IsVirtualItem || !string.IsNullOrEmpty(film.Path))
        {
            _logger.LogWarning("Not deleting {Name} ({Id}) from the discovery folder: it is not a virtual title", film.Name, film.Id);
            return;
        }

        // Extras (pushed trailers) are owned by the title, not its children, so DeleteItem leaves them behind.
        // Their files belong to the app that pushed them, so only the items go.
        var extras = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeDiscovery = true,
            OwnerIds = [film.Id],
        });
        var options = new DeleteOptions { DeleteFileLocation = false };
        foreach (var extra in extras)
        {
            _libraryManager.DeleteItem(extra, options, false);
        }

        _logger.LogInformation("Deleting discovery title {Name} ({Id}), in no list", film.Name, film.Id);
        _libraryManager.DeleteItem(film, options, false);
    }
}
