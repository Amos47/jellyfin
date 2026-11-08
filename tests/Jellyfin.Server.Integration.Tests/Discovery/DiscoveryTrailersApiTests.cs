using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

public sealed class DiscoveryTrailersApiTests : IClassFixture<JellyfinApplicationFactory>, IDisposable
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly string _tempRoot;
    private readonly string _trailerRoot;

    public DiscoveryTrailersApiTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;

        // Under the system temp folder, which on macOS sits behind the /var -> /private/var symlink.
        _tempRoot = Path.Combine(Path.GetTempPath(), "jellyfin-discovery-trailers-" + Guid.NewGuid().ToString("N")[..8]);
        _trailerRoot = Path.Combine(_tempRoot, "discovery");
        Directory.CreateDirectory(_trailerRoot);
        SetTrailerRoot(_trailerRoot);
    }

    private IDiscoveryFilmSync Sync => _factory.Services.GetRequiredService<IDiscoveryFilmSync>();

    private ILibraryManager LibraryManager => _factory.Services.GetRequiredService<ILibraryManager>();

    public void Dispose()
    {
        SetTrailerRoot(null);
        Directory.Delete(_tempRoot, true);
    }

    [Fact]
    public async Task DiscoveryFilm_PlayAccessNone()
    {
        var (client, userId) = await SetUpAsync();
        try
        {
            await PutListAsync(client, "play-none", 810);
            var film = Sync.FindFilm(810);
            Assert.NotNull(film);

            var dto = await GetAsync<BaseItemDto>(client, $"Items/{film.Id:N}?userId={userId:N}");

            Assert.Equal(PlayAccess.None, dto.PlayAccess);
        }
        finally
        {
            await DeleteListAsync(client, "play-none");
        }
    }

    [Fact]
    public async Task OwnedFilm_PlayAccessFull()
    {
        var (client, userId) = await SetUpAsync();
        var seed = await DiscoverySeed.EnsureAsync(_factory);

        var dto = await GetAsync<BaseItemDto>(client, $"Items/{seed.RealFilm.Id:N}?userId={userId:N}");

        Assert.Equal(PlayAccess.Full, dto.PlayAccess);
    }

    [Fact]
    public async Task SetTrailer_ThenLocalTrailers_ReturnsIt()
    {
        var (client, userId) = await SetUpAsync();
        var file = await WriteVideoAsync(Path.Combine(_trailerRoot, "Film (2020)", "Film (2020)-trailer.mkv"));
        try
        {
            await PutListAsync(client, "trailers", 811);
            var film = Sync.FindFilm(811);
            Assert.NotNull(film);

            await SetTrailerAsync(client, 811, file);

            // Setting the same file again changes nothing.
            await SetTrailerAsync(client, 811, file);

            var trailers = await GetAsync<BaseItemDto[]>(client, $"Items/{film.Id:N}/LocalTrailers?userId={userId:N}");
            var trailer = Assert.Single(trailers);
            var item = LibraryManager.GetItemById(trailer.Id);
            Assert.NotNull(item);
            Assert.IsType<Video>(item);
            Assert.Equal(ExtraType.Trailer, item.ExtraType);
            Assert.Equal(film.Id, item.OwnerId);
            Assert.True(item.ParentId.IsEmpty());
            Assert.Equal(ResolvedPath(file), item.Path);
            Assert.Equal(LibraryManager.GetNewItemId(item.Path, typeof(Video)), item.Id);

            var dto = await GetAsync<BaseItemDto>(client, $"Items/{film.Id:N}?userId={userId:N}");
            Assert.Equal(1, dto.LocalTrailerCount);

            // A second file replaces the first: a film has one pushed trailer.
            var second = await WriteVideoAsync(Path.Combine(_trailerRoot, "Film (2020)", "Film (2020)-trailer2.mkv"));
            await SetTrailerAsync(client, 811, second);
            trailers = await GetAsync<BaseItemDto[]>(client, $"Items/{film.Id:N}/LocalTrailers?userId={userId:N}");
            Assert.Equal(ResolvedPath(second), LibraryManager.GetItemById(Assert.Single(trailers).Id)?.Path);
            Assert.Null(LibraryManager.GetItemById(trailer.Id));
            Assert.True(File.Exists(file));

            using (var delete = await client.DeleteAsync("Discovery/Films/811/Trailer", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            Assert.Empty(await GetAsync<BaseItemDto[]>(client, $"Items/{film.Id:N}/LocalTrailers?userId={userId:N}"));
            Assert.True(File.Exists(file));
            Assert.True(File.Exists(second));

            using var missing = await SendSetTrailerAsync(client, 999811, file);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await DeleteListAsync(client, "trailers");
        }
    }

    [Fact]
    public async Task SetTrailer_OutsideRoot_BadRequest()
    {
        var (client, _) = await SetUpAsync();
        var outsideDir = Path.Combine(_tempRoot, "elsewhere");
        var outside = await WriteVideoAsync(Path.Combine(outsideDir, "outside-trailer.mkv"));

        // A sibling folder whose name starts with the root's name is not inside it.
        var sibling = await WriteVideoAsync(Path.Combine(_tempRoot, "discovery2", "sibling-trailer.mkv"));

        var fileLink = Path.Combine(_trailerRoot, "link-trailer.mkv");
        File.CreateSymbolicLink(fileLink, outside);
        var dirLink = Path.Combine(_trailerRoot, "linked-dir");
        Directory.CreateSymbolicLink(dirLink, outsideDir);
        try
        {
            await PutListAsync(client, "outside", 812);
            var film = Sync.FindFilm(812);
            Assert.NotNull(film);

            string[] refused =
            [
                Path.Combine(_trailerRoot, "..", "elsewhere", "outside-trailer.mkv"),
                outside,
                sibling,
                fileLink,
                Path.Combine(dirLink, "outside-trailer.mkv"),
                Path.Combine(_trailerRoot, "missing-trailer.mkv"),
                Path.Combine("discovery", "relative-trailer.mkv"),
                _trailerRoot,
                string.Empty,
            ];
            foreach (var path in refused)
            {
                using var response = await SendSetTrailerAsync(client, 812, path);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{path}: {response.StatusCode}");
            }

            Assert.Empty(GetExtras(film.Id));

            // A link that stays inside the root is fine; the trailer gets the real path.
            var inside = await WriteVideoAsync(Path.Combine(_trailerRoot, "Real (2022)", "Real (2022)-trailer.mkv"));
            var insideLink = Path.Combine(_trailerRoot, "inside-link-trailer.mkv");
            File.CreateSymbolicLink(insideLink, inside);
            await SetTrailerAsync(client, 812, insideLink);
            Assert.Equal(ResolvedPath(inside), Assert.Single(GetExtras(film.Id)).Path);
        }
        finally
        {
            await DeleteListAsync(client, "outside");
        }

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task SetTrailer_NoRootConfigured_BadRequest()
    {
        var (client, _) = await SetUpAsync();
        var file = await WriteVideoAsync(Path.Combine(_trailerRoot, "noroot-trailer.mkv"));
        try
        {
            await PutListAsync(client, "noroot", 813);
            var film = Sync.FindFilm(813);
            Assert.NotNull(film);

            SetTrailerRoot(null);
            using (var response = await SendSetTrailerAsync(client, 813, file))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            SetTrailerRoot("   ");
            using (var response = await SendSetTrailerAsync(client, 813, file))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            Assert.Empty(GetExtras(film.Id));
        }
        finally
        {
            SetTrailerRoot(_trailerRoot);
            await DeleteListAsync(client, "noroot");
        }
    }

    [Fact]
    public async Task Trailer_HiddenFromItems()
    {
        var (client, userId) = await SetUpAsync();
        var file = await WriteVideoAsync(Path.Combine(_trailerRoot, "hidden-trailer.mkv"));
        try
        {
            await PutListAsync(client, "hidden", 814);
            await SetTrailerAsync(client, 814, file);
            var trailerId = LibraryManager.GetNewItemId(ResolvedPath(file), typeof(Video));
            Assert.NotNull(LibraryManager.GetItemById(trailerId));

            var videos = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?recursive=true&includeItemTypes=Video&userId={userId:N}");
            var all = await GetAsync<QueryResult<BaseItemDto>>(client, $"Items?recursive=true&userId={userId:N}");

            Assert.DoesNotContain(videos.Items, i => i.Id.Equals(trailerId));
            Assert.DoesNotContain(all.Items, i => i.Id.Equals(trailerId));
        }
        finally
        {
            await DeleteListAsync(client, "hidden");
        }
    }

    [Fact]
    public async Task DeleteList_DeletesTrailerItem()
    {
        var (client, _) = await SetUpAsync();
        var file = await WriteVideoAsync(Path.Combine(_trailerRoot, "Gone (2021)", "Gone (2021)-trailer.mkv"));
        try
        {
            await PutListAsync(client, "gone", 815);
            var film = Sync.FindFilm(815);
            Assert.NotNull(film);
            await SetTrailerAsync(client, 815, file);
            var trailerId = LibraryManager.GetNewItemId(ResolvedPath(file), typeof(Video));
            Assert.NotNull(LibraryManager.GetItemById(trailerId));

            await DeleteListAsync(client, "gone");

            Assert.Null(Sync.FindFilm(815));
            Assert.Null(LibraryManager.GetItemById(trailerId));
            Assert.Empty(GetExtras(film.Id));
            Assert.True(File.Exists(file));
        }
        finally
        {
            await DeleteListAsync(client, "gone");
        }
    }

    private static string ResolvedPath(string path)
    {
        // The temp folder may sit behind a symlink (macOS /var), so the stored path is the resolved one.
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var parts = new System.Collections.Generic.Stack<string>();
        while (directory is not null && directory.LinkTarget is null)
        {
            parts.Push(directory.Name);
            directory = directory.Parent;
        }

        var resolvedRoot = directory is null ? null : directory.ResolveLinkTarget(true)!.FullName;
        var resolved = resolvedRoot is null ? Path.GetDirectoryName(path)! : Path.Combine([resolvedRoot, .. parts]);
        return Path.Combine(resolved, Path.GetFileName(path));
    }

    private static async Task<string> WriteVideoAsync(string path)
    {
        // No video in the test data: a placeholder file. The probe may fail; the tests assert on items only.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[1024], TestContext.Current.CancellationToken);
        return path;
    }

    private BaseItem[] GetExtras(Guid filmId)
        => LibraryManager.GetItemList(new InternalItemsQuery { IncludeDiscovery = true, OwnerIds = [filmId] }).ToArray();

    private void SetTrailerRoot(string? root)
        => _factory.Services.GetRequiredService<IConfigurationManager>()
            .SaveConfiguration("discovery", new DiscoveryOptions { TrailerRoot = root });

    private static Task<HttpResponseMessage> SendSetTrailerAsync(HttpClient client, int tmdbId, string path)
        => client.PutAsJsonAsync($"Discovery/Films/{tmdbId}/Trailer", new { Path = path }, JsonDefaults.Options, TestContext.Current.CancellationToken);

    private static async Task SetTrailerAsync(HttpClient client, int tmdbId, string path)
    {
        using var response = await SendSetTrailerAsync(client, tmdbId, path);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task PutListAsync(HttpClient client, string listId, int tmdbId)
    {
        using var response = await client.PutAsJsonAsync(
            "Discovery/Lists/" + listId,
            new { Title = listId, Source = "tests", Films = new[] { new { TmdbId = tmdbId } } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task DeleteListAsync(HttpClient client, string listId)
    {
        using var response = await client.DeleteAsync("Discovery/Lists/" + listId, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"DELETE {listId}: {response.StatusCode}");
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return value;
    }

    private async Task<(HttpClient Client, Guid UserId)> SetUpAsync()
    {
        var seed = await DiscoverySeed.EnsureAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(seed.AccessToken);
        var user = await AuthHelper.GetUserDtoAsync(client);
        return (client, user.Id);
    }
}
