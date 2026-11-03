using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Configuration;
using MediaBrowser.Common.Extensions;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Configuration;

public class UpstreamHomeSectionMapperTests
{
    internal static HomeSection Section(int id, int order, HomeSectionType type)
    {
        var section = new HomeSection { Order = order, Type = type };
        typeof(HomeSection).GetProperty(nameof(HomeSection.Id))!.SetValue(section, id);
        return section;
    }

    [Fact]
    public void Map_EmptySections_UsesStandardOrder()
    {
        var views = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var rows = UpstreamHomeSectionMapper.Map([], views);

        Assert.Equal(
            new[]
            {
                HomeRowProviderIds.LibraryTiles,
                HomeRowProviderIds.Resume,
                HomeRowProviderIds.ResumeAudio,
                HomeRowProviderIds.ResumeBook,
                HomeRowProviderIds.LiveTv,
                HomeRowProviderIds.NextUp,
                HomeRowProviderIds.LatestMedia,
                HomeRowProviderIds.LatestMedia,
            },
            rows.Select(r => r.Provider));
        Assert.All(rows, r =>
        {
            Assert.True(r.Enabled);
            Assert.Null(r.Title);
            Assert.False(r.Id.Equals(Guid.Empty));
        });
        Assert.All(rows.Where(r => r.Provider != HomeRowProviderIds.LatestMedia), r => Assert.Empty(r.Config));
    }

    [Fact]
    public void Map_ExpandsLatestPerView()
    {
        var views = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var rows = UpstreamHomeSectionMapper.Map([Section(1, 0, HomeSectionType.LatestMedia)], views);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(HomeRowProviderIds.LatestMedia, r.Provider));
        Assert.Equal(
            views.Select(v => v.ToString("N", CultureInfo.InvariantCulture)),
            rows.Select(r => (string?)r.Config["library"]));
        Assert.All(rows, r => Assert.Single(r.Config));
        Assert.Equal(3, rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Map_SkipsNoneSortsByOrderAndHandlesGaps()
    {
        HomeSection[] sections =
        [
            Section(5, 2, HomeSectionType.NextUp),
            Section(1, 9, HomeSectionType.LiveTv),
            Section(2, 1, HomeSectionType.None),
            Section(4, 0, HomeSectionType.SmallLibraryTiles),
        ];

        var rows = UpstreamHomeSectionMapper.Map(sections, [Guid.NewGuid()]);

        Assert.Equal(
            new[] { HomeRowProviderIds.LibraryTiles, HomeRowProviderIds.NextUp, HomeRowProviderIds.LiveTv },
            rows.Select(r => r.Provider));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Map_DuplicateOrders_LastWrittenSectionWinsLikeUpstream()
    {
        // Upstream accumulates duplicate HomeSection rows per order; its GET writes
        // CustomPrefs["homesection" + Order] in id order, so the highest id wins each slot.
        HomeSection[] sections =
        [
            Section(1, 0, HomeSectionType.LibraryButtons),
            Section(2, 1, HomeSectionType.NextUp),
            Section(3, 2, HomeSectionType.NextUp),
            Section(4, 0, HomeSectionType.LibraryButtons),
            Section(5, 1, HomeSectionType.Resume),
            Section(6, 2, HomeSectionType.None),
            Section(7, 0, HomeSectionType.LibraryButtons),
        ];

        var rows = UpstreamHomeSectionMapper.Map(sections, []);

        Assert.Equal(
            new[] { HomeRowProviderIds.LibraryButtons, HomeRowProviderIds.Resume },
            rows.Select(r => r.Provider));
    }

    [Fact]
    public void Map_SameTypeInDifferentOrders_KeepsEachRow()
    {
        var rows = UpstreamHomeSectionMapper.Map(
            [Section(1, 0, HomeSectionType.Resume), Section(2, 3, HomeSectionType.Resume)],
            []);

        Assert.Equal(new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.Resume }, rows.Select(r => r.Provider));
        Assert.False(rows[0].Id.Equals(rows[1].Id));
    }

    [Fact]
    public void Map_MapsEveryType()
    {
        var types = new Dictionary<HomeSectionType, string>
        {
            [HomeSectionType.SmallLibraryTiles] = HomeRowProviderIds.LibraryTiles,
            [HomeSectionType.LibraryButtons] = HomeRowProviderIds.LibraryButtons,
            [HomeSectionType.ActiveRecordings] = HomeRowProviderIds.ActiveRecordings,
            [HomeSectionType.Resume] = HomeRowProviderIds.Resume,
            [HomeSectionType.ResumeAudio] = HomeRowProviderIds.ResumeAudio,
            [HomeSectionType.ResumeBook] = HomeRowProviderIds.ResumeBook,
            [HomeSectionType.NextUp] = HomeRowProviderIds.NextUp,
            [HomeSectionType.LiveTv] = HomeRowProviderIds.LiveTv,
        };

        var rows = UpstreamHomeSectionMapper.Map(types.Keys.Select((t, i) => Section(i + 1, i, t)).ToArray(), []);

        Assert.Equal(types.Values, rows.Select(r => r.Provider));
    }

    [Fact]
    public void Map_LatestWithoutViews_AddsNoRows()
    {
        var rows = UpstreamHomeSectionMapper.Map([Section(1, 0, HomeSectionType.LatestMedia), Section(2, 1, HomeSectionType.Resume)], []);

        Assert.Equal(new[] { HomeRowProviderIds.Resume }, rows.Select(r => r.Provider));
    }

    [Fact]
    public void Map_CapsAtTwentyRows()
    {
        var views = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();

        var rows = UpstreamHomeSectionMapper.Map([], views);

        Assert.Equal(20, rows.Count);
        Assert.Equal(HomeRowProviderIds.NextUp, rows[5].Provider);
        Assert.Equal(
            views.Take(14).Select(v => v.ToString("N", CultureInfo.InvariantCulture)),
            rows.Skip(6).Select(r => (string?)r.Config["library"]));
    }

    [Fact]
    public void Map_IdsAreStableAcrossCalls()
    {
        var views = new[] { Guid.NewGuid(), Guid.NewGuid() };
        HomeSection[] sections = [Section(1, 0, HomeSectionType.Resume), Section(2, 1, HomeSectionType.LatestMedia)];

        Assert.Equal(
            UpstreamHomeSectionMapper.Map(sections, views).Select(r => r.Id),
            UpstreamHomeSectionMapper.Map(sections, views).Select(r => r.Id));
        Assert.Equal(
            UpstreamHomeSectionMapper.Map([], views).Select(r => r.Id),
            UpstreamHomeSectionMapper.Map([], views).Select(r => r.Id));
        Assert.True(UpstreamHomeSectionMapper.Map([], views)[0].Id.Equals("upstream:0:library-tiles:".GetMD5()));
    }
}
