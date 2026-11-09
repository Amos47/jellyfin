using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

// Fork: a show with no episodes (a discovery show, or one still downloading) isn't watched.
public class FolderPlayedStatusTests
{
    [Fact]
    public void FillUserDataDtoValues_SeriesWithNoEpisodes_IsNotPlayed()
    {
        var dto = Fill(new Series { Id = Guid.NewGuid() }, (0, 0));

        Assert.False(dto.Played);
        Assert.Equal(0, dto.UnplayedItemCount);
    }

    [Fact]
    public void FillUserDataDtoValues_SeriesWithAllEpisodesPlayed_IsPlayed()
    {
        var dto = Fill(new Series { Id = Guid.NewGuid() }, (3, 3));

        Assert.True(dto.Played);
    }

    [Fact]
    public void FillUserDataDtoValues_EmptyFolder_StaysPlayed()
    {
        var dto = Fill(new Folder { Id = Guid.NewGuid() }, (0, 0));

        Assert.True(dto.Played);
    }

    private static UserItemDataDto Fill(Folder folder, (int Played, int Total) counts)
    {
        var dto = new UserItemDataDto { Key = folder.Id.ToString("N"), ItemId = folder.Id };
        var user = new User("test", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        folder.FillUserDataDtoValues(dto, new UserItemData { Key = dto.Key }, null, user, new DtoOptions(), counts);
        return dto;
    }
}
