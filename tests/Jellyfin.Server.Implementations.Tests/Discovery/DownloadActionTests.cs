using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Discovery;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

public class DownloadActionTests
{
    private readonly User _user = new("tester", "Default", "Default");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Perform_FilmGoneBeforeTheLock_PerformsNothing()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        film.SetProviderId(MetadataProvider.Tmdb, 77.ToString(CultureInfo.InvariantCulture));

        var requests = new Mock<IDiscoveryRequestStore>();
        var lists = new Mock<IUserListStore>();
        var sync = new Mock<IDiscoveryFilmSync>();

        // Found when the action is resolved, gone once the lock is held (a sync deleted it in between).
        sync.SetupSequence(s => s.FindFilm(77)).Returns(film).Returns((Movie?)null);
        sync.Setup(s => s.RunExclusiveAsync(It.IsAny<Func<Task<DiscoveryRequest?>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task<DiscoveryRequest?>> work, CancellationToken _) => work());

        var action = new DownloadAction(requests.Object, lists.Object, sync.Object);

        Assert.Null(await action.PerformAsync(_user, film, Ct));
        requests.Verify(r => r.RequestAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        lists.Verify(l => l.AddFilmAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
