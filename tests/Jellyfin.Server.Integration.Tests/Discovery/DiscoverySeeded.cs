using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Server.Integration.Tests.Discovery;

/// <summary>
/// What <see cref="DiscoverySeed.EnsureAsync"/> created.
/// </summary>
/// <param name="AccessToken">An admin access token (the wizard's user).</param>
/// <param name="FolderId">The discovery folder id.</param>
/// <param name="SeedFilm">The virtual discovery film.</param>
/// <param name="SeedTrailer">A trailer extra owned by the virtual film.</param>
/// <param name="RealFilm">The real library film.</param>
/// <param name="Library">The temporary movie library.</param>
public sealed record DiscoverySeeded(string AccessToken, Guid FolderId, Movie SeedFilm, Trailer SeedTrailer, Movie RealFilm, CollectionFolder Library);
