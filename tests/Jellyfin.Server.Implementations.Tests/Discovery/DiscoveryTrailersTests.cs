using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

/// <summary>
/// Fork: discovery. Attaching a trailer while a list sync deletes its film answers "film not found", not an error.
/// </summary>
public sealed class DiscoveryTrailersTests : IDisposable
{
    private const int TmdbId = 4242;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "discovery-trailers-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IDiscoveryFilmSync> _sync = new();
    private readonly Mock<IDiscoveryMetadataRefresher> _refresher = new();
    private readonly Movie _film = new() { Id = Guid.NewGuid(), Name = "Film" };
    private readonly string _file;
    private readonly DiscoveryTrailers _subject;

    public DiscoveryTrailersTests()
    {
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "trailer.mkv");
        File.WriteAllBytes(_file, new byte[16]);

        var configuration = new Mock<IConfigurationManager>();
        configuration.Setup(c => c.GetConfiguration(DiscoveryConfigurationFactory.Key)).Returns(new DiscoveryOptions { TrailerRoot = _root });
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetServerLocalizedString(It.IsAny<string>())).Returns("Trailer");
        _libraryManager.Setup(m => m.GetNewItemId(It.IsAny<string>(), typeof(Video))).Returns(Guid.NewGuid());
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());

        _subject = new DiscoveryTrailers(
            _libraryManager.Object,
            _sync.Object,
            Mock.Of<IDiscoveryFolderLocator>(),
            configuration.Object,
            _refresher.Object,
            localization.Object,
            NullLogger<DiscoveryTrailers>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _subject.Dispose();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Set_FilmDeletedBeforeTheLock_FilmNotFound()
    {
        // Found on the first look, gone by the time the lock is held.
        _sync.SetupSequence(s => s.FindFilm(TmdbId)).Returns(_film).Returns((Movie?)null);

        var result = await _subject.SetAsync(TmdbId, _file, Ct);

        Assert.Equal(DiscoveryTrailerResult.FilmNotFound, result);
        _libraryManager.Verify(m => m.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem?>()), Times.Never);
        _refresher.Verify(r => r.QueueProbe(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Set_FilmDeletedDuringTheSave_FilmNotFound()
    {
        // The sync deletes the film after the last look: saving the trailer breaks its owner foreign key.
        _sync.SetupSequence(s => s.FindFilm(TmdbId)).Returns(_film).Returns(_film).Returns((Movie?)null);
        _libraryManager.Setup(m => m.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem?>()))
            .Throws(new DbUpdateException("FOREIGN KEY constraint failed"));

        var result = await _subject.SetAsync(TmdbId, _file, Ct);

        Assert.Equal(DiscoveryTrailerResult.FilmNotFound, result);
        _refresher.Verify(r => r.QueueProbe(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Set_SaveFailsWithTheFilmStillThere_Throws()
    {
        // Any other save failure is not hidden as a missing film.
        _sync.Setup(s => s.FindFilm(TmdbId)).Returns(_film);
        _libraryManager.Setup(m => m.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem?>()))
            .Throws(new DbUpdateException("disk full"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _subject.SetAsync(TmdbId, _file, Ct));
    }
}
