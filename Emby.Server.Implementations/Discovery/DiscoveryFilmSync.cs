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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Fork: creates a pathless virtual <see cref="Movie"/> in the discovery folder for every TMDB id in a discovery
/// list, and deletes the virtual films (with their extras) that are in no list. Library films are never touched.
/// </summary>
public sealed class DiscoveryFilmSync : IDiscoveryFilmSync, IDisposable
{
    private const string IdPrefix = "discovery-tmdb-";

    private readonly ILibraryManager _libraryManager;
    private readonly IDiscoveryStore _store;
    private readonly IDiscoveryFolderLocator _folderLocator;
    private readonly IDiscoveryMetadataRefresher _refresher;
    private readonly ILogger<DiscoveryFilmSync> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryFilmSync"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The discovery store.</param>
    /// <param name="folderLocator">The discovery folder locator.</param>
    /// <param name="refresher">The metadata refresher.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryFilmSync(
        ILibraryManager libraryManager,
        IDiscoveryStore store,
        IDiscoveryFolderLocator folderLocator,
        IDiscoveryMetadataRefresher refresher,
        ILogger<DiscoveryFilmSync> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
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

            // TMDB id -> IMDb id (the first one any list gives).
            var wanted = new Dictionary<int, string?>();
            foreach (var film in lists.SelectMany(l => l.Films))
            {
                if (!wanted.TryGetValue(film.TmdbId, out var imdbId) || string.IsNullOrEmpty(imdbId))
                {
                    wanted[film.TmdbId] = string.IsNullOrEmpty(film.ImdbId) ? null : film.ImdbId;
                }
            }

            var wantedIds = wanted.Keys.Select(GetFilmId).ToHashSet();
            var folder = _folderLocator.GetFolder();

            foreach (var film in GetFilms(folder.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!wantedIds.Contains(film.Id))
                {
                    DeleteFilm(film);
                }
            }

            foreach (var (tmdbId, imdbId) in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = FindFilm(tmdbId);
                if (existing is null)
                {
                    CreateFilm(folder, tmdbId, imdbId);
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
    public Movie? FindFilm(int tmdbId)
        => _libraryManager.GetItemById(GetFilmId(tmdbId)) as Movie;

    /// <inheritdoc />
    public BaseItem? FindOwned(User user, int tmdbId, string? imdbId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var providerIds = new Dictionary<string, string>
        {
            [MetadataProvider.Tmdb.ToString()] = tmdbId.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(imdbId))
        {
            providerIds[MetadataProvider.Imdb.ToString()] = imdbId;
        }

        // A normal user query: the discovery guard keeps the virtual films out, so only library films match.
        var owned = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            HasAnyProviderId = providerIds,
            Limit = 1,
            DtoOptions = new DtoOptions(false),
        });
        return owned.Count > 0 ? owned[0] : null;
    }

    private Guid GetFilmId(int tmdbId)
        => _libraryManager.GetNewItemId(IdPrefix + tmdbId.ToString(CultureInfo.InvariantCulture), typeof(Movie));

    private IReadOnlyList<BaseItem> GetFilms(Guid folderId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeDiscovery = true,
            ParentId = folderId,
            IncludeItemTypes = [BaseItemKind.Movie],
        });

    private void CreateFilm(BasePluginFolder folder, int tmdbId, string? imdbId)
    {
        // No path: pathless items are never removed by library scans.
        var film = new Movie
        {
            Id = GetFilmId(tmdbId),
            Name = "TMDB " + tmdbId.ToString(CultureInfo.InvariantCulture),
            IsVirtualItem = true,
        };
        film.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));
        if (imdbId is not null)
        {
            film.SetProviderId(MetadataProvider.Imdb, imdbId);
        }

        _logger.LogInformation("Creating discovery film for TMDB id {TmdbId}", tmdbId);
        folder.AddChild(film);
        _refresher.QueueRefresh(film.Id);
    }

    private void DeleteFilm(BaseItem film)
    {
        // Only virtual pathless films are ever deleted here, whatever else ends up in the folder.
        if (!film.IsVirtualItem || !string.IsNullOrEmpty(film.Path))
        {
            _logger.LogWarning("Not deleting {Name} ({Id}) from the discovery folder: it is not a virtual film", film.Name, film.Id);
            return;
        }

        // Extras (pushed trailers) are owned by the film, not its children, so DeleteItem leaves them behind.
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

        _logger.LogInformation("Deleting discovery film {Name} ({Id}), in no list", film.Name, film.Id);
        _libraryManager.DeleteItem(film, options, false);
    }
}
