using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class ResumeQueryFactoryTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly User _user = new("tester", "Default", "Default");

    private SessionInfo Session(Guid userId, Guid? nowPlaying)
        => new(_sessionManager.Object, NullLogger.Instance)
        {
            UserId = userId,
            NowPlayingItem = nowPlaying is { } id ? new BaseItemDto { Id = id } : null!
        };

    [Fact]
    public void Create_ExcludesTheUsersActiveSessionItems()
    {
        var playing = Guid.NewGuid();
        var playingAudio = Guid.NewGuid();
        _sessionManager.SetupGet(s => s.Sessions).Returns(
        [
            Session(_user.Id, playing),
            Session(_user.Id, playingAudio),
            Session(_user.Id, playing),
            Session(_user.Id, null),
            Session(Guid.NewGuid(), Guid.NewGuid())
        ]);

        var query = ResumeQueryFactory.Create(_user, _libraryManager.Object, _sessionManager.Object, MediaType.Video, 0, 10);

        Assert.Equal(new[] { playing, playingAudio }.Order(), query.ExcludeItemIds.Order());
    }

    [Fact]
    public void Create_WithoutSessionManager_ExcludesNothing()
    {
        var query = ResumeQueryFactory.Create(_user, _libraryManager.Object, null, MediaType.Video, 0, 10);

        Assert.Empty(query.ExcludeItemIds);
    }

    [Fact]
    public void Create_NoActiveSessions_ExcludesNothing()
    {
        _sessionManager.SetupGet(s => s.Sessions).Returns(Array.Empty<SessionInfo>());

        var query = ResumeQueryFactory.Create(_user, _libraryManager.Object, _sessionManager.Object, MediaType.Video, 0, 10);

        Assert.Empty(query.ExcludeItemIds);
    }
}
