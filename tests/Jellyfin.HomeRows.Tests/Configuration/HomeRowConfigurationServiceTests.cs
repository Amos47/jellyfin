using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Jellyfin.HomeRows.Tests.Configuration.UpstreamHomeSectionMapperTests;

namespace Jellyfin.HomeRows.Tests.Configuration;

public sealed class HomeRowConfigurationServiceTests : IDisposable
{
    private const string Gone = "gone-provider";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"homerows-{Guid.NewGuid():N}.db");
    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly Mock<IDisplayPreferencesManager> _displayPreferences = new();
    private readonly Mock<IUserViewManager> _userViews = new();
    private readonly Dictionary<Guid, DisplayPreferences> _preferences = [];
    private readonly User _user = new("alice", "Default", "Default");
    private readonly User _other = new("bob", "Default", "Default");
    private readonly User _admin = new("admin", "Default", "Default");
    private readonly Guid _movies = Guid.NewGuid();
    private readonly Guid _shows = Guid.NewGuid();

    public HomeRowConfigurationServiceTests()
    {
        var options = new DbContextOptionsBuilder<CustomDbContext>()
            .UseSqlite(
                $"Data Source={_dbPath}",
                o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly).MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName))
            .Options;
        _dbFactory = new TestDbFactory(options);
        using (var context = _dbFactory.CreateDbContext())
        {
            context.Database.Migrate();
        }

        _displayPreferences
            .Setup(m => m.GetDisplayPreferences(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((Guid userId, Guid itemId, string client) => GetPreferences(userId, itemId, client));
        _userViews
            .Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>()))
            .Returns(() =>
            [
                new CollectionFolder { Id = _movies, CollectionType = CollectionType.movies },
                new CollectionFolder { Id = _shows, CollectionType = CollectionType.tvshows },
            ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task Effective_NoAnything_StandardDefault()
    {
        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.StandardDefault, layout.Source);
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
            layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(new[] { Library(_movies), Library(_shows) }, layout.Rows.Skip(6).Select(r => (string?)r.Row.Config["library"]));
        Assert.True(layout.Rows[1].IsAvailable);
        Assert.False(layout.Rows[0].IsAvailable);
    }

    [Fact]
    public async Task Effective_OnlyNoneSections_StandardDefault()
    {
        GetPreferences(_user.Id, "usersettings".GetMD5(), "emby").HomeSections.Add(Section(1, 0, HomeSectionType.None));

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.StandardDefault, layout.Source);
    }

    [Fact]
    public async Task Effective_UpstreamSections_UsedWhenNoAdminDefault()
    {
        var sections = GetPreferences(_user.Id, "usersettings".GetMD5(), "emby").HomeSections;
        sections.Add(Section(1, 1, HomeSectionType.Resume));
        sections.Add(Section(2, 0, HomeSectionType.LatestMedia));
        sections.Add(Section(3, 2, HomeSectionType.None));
        GetPreferences(_user.Id, "usersettings".GetMD5(), "other-client").HomeSections.Add(Section(4, 0, HomeSectionType.NextUp));

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.UpstreamSections, layout.Source);
        Assert.Equal(
            new[] { HomeRowProviderIds.LatestMedia, HomeRowProviderIds.LatestMedia, HomeRowProviderIds.Resume },
            layout.Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Effective_AdminDefault_BeatsUpstreamSections()
    {
        GetPreferences(_user.Id, "usersettings".GetMD5(), "emby").HomeSections.Add(Section(1, 0, HomeSectionType.Resume));
        var service = CreateService();

        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.NextUp)], Ct));
        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.AdminDefault, layout.Source);
        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, (await service.GetAdminDefaultAsync(Ct))!.Select(r => r.Provider));

        await using var context = await _dbFactory.CreateDbContextAsync(Ct);
        var stored = await context.HomeRowDefaults.SingleAsync(Ct);
        Assert.Equal(1, stored.Id);
        Assert.True(stored.ModifiedByUserId.Equals(_admin.Id));
    }

    [Fact]
    public async Task Effective_CustomizedUser_IgnoresAdminDefaultChanges()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.NextUp)], Ct));
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.Resume)], Ct));

        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.LatestMedia, new JsonObject { ["library"] = "x" })], Ct));
        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        var inheriting = await service.GetEffectiveLayoutAsync(_other, Ct);

        Assert.Equal(HomeRowLayoutSource.User, layout.Source);
        Assert.Equal(new[] { HomeRowProviderIds.Resume }, layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(HomeRowLayoutSource.AdminDefault, inheriting.Source);
        Assert.Equal(new[] { HomeRowProviderIds.LatestMedia }, inheriting.Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Effective_InheritingUser_FollowsAdminDefaultChange()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.NextUp)], Ct));
        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, (await service.GetEffectiveLayoutAsync(_user, Ct)).Rows.Select(r => r.Row.Provider));

        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.Resume), Row(HomeRowProviderIds.NextUp)], Ct));
        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.AdminDefault, layout.Source);
        Assert.Equal(new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.NextUp }, layout.Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Save_AssignsIdsAndNormalizesConfig()
    {
        var keptId = Guid.NewGuid();
        var service = CreateService();

        var errors = await service.SaveUserLayoutAsync(
            _user,
            [
                new HomeRowInstance(Guid.Empty, HomeRowProviderIds.Resume, "  My row  ", new JsonObject { ["limit"] = 5, ["junk"] = true }, true),
                new HomeRowInstance(keptId, HomeRowProviderIds.Resume, "   ", new JsonObject(), false),
            ],
            Ct);

        Assert.Empty(errors);
        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        Assert.Equal(HomeRowLayoutSource.User, layout.Source);
        var first = layout.Rows[0].Row;
        var second = layout.Rows[1].Row;
        Assert.False(first.Id.Equals(Guid.Empty));
        Assert.Equal("My row", first.Title);
        Assert.Equal("{\"limit\":5}", first.Config.ToJsonString());
        Assert.True(first.Enabled);
        Assert.True(second.Id.Equals(keptId));
        Assert.Null(second.Title);
        Assert.Equal("{\"limit\":10}", second.Config.ToJsonString());
        Assert.False(second.Enabled);

        await using var context = await _dbFactory.CreateDbContextAsync(Ct);
        var stored = await context.HomeRowConfigurations.SingleAsync(Ct);
        Assert.True(stored.UserId.Equals(_user.Id));
        var json = JsonNode.Parse(stored.RowsJson)!.AsArray();
        Assert.Equal(
            new[] { "id", "provider", "title", "config", "enabled" },
            json[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(keptId.ToString(), (string?)json[1]!["id"]);
    }

    [Fact]
    public async Task Save_InvalidConfig_ReturnsErrorsAndStoresNothing()
    {
        var service = CreateService();

        var errors = await service.SaveUserLayoutAsync(
            _user,
            [
                Row(HomeRowProviderIds.Resume),
                Row(HomeRowProviderIds.Resume, new JsonObject { ["limit"] = 500 }),
                Row(HomeRowProviderIds.LatestMedia),
            ],
            Ct);

        Assert.Equal(new[] { (1, "limit"), (2, "library") }, errors.Select(e => (e.RowIndex, e.Key)));
        Assert.All(errors, e => Assert.False(string.IsNullOrEmpty(e.Message)));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Save_InvalidTitleOrProvider_Errors()
    {
        var service = CreateService();

        var errors = await service.SaveUserLayoutAsync(
            _user,
            [
                Row(HomeRowProviderIds.Resume) with { Title = new string('a', 101) },
                Row(string.Empty),
                Row("never-registered"),
            ],
            Ct);

        Assert.Equal(new[] { (0, "title"), (1, "provider"), (2, "provider") }, errors.Select(e => (e.RowIndex, e.Key)));
        await AssertNothingStoredAsync();

        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.Resume) with { Title = " " + new string('a', 100) + " " }], Ct));
    }

    [Fact]
    public async Task Save_MoreThanTwentyRows_Error()
    {
        var service = CreateService();

        var errors = await service.SaveUserLayoutAsync(_user, Enumerable.Range(0, 21).Select(_ => Row(HomeRowProviderIds.Resume)).ToArray(), Ct);

        Assert.Equal("rows", Assert.Single(errors).Key);
        await AssertNothingStoredAsync();
        Assert.Empty(await service.SaveUserLayoutAsync(_user, Enumerable.Range(0, 20).Select(_ => Row(HomeRowProviderIds.Resume)).ToArray(), Ct));
    }

    [Fact]
    public async Task Save_DuplicateIds_Error()
    {
        var id = Guid.NewGuid();
        var service = CreateService();

        var errors = await service.SaveUserLayoutAsync(
            _user,
            [Row(HomeRowProviderIds.Resume) with { Id = id }, Row(HomeRowProviderIds.NextUp), Row(HomeRowProviderIds.NextUp) with { Id = id }],
            Ct);

        var error = Assert.Single(errors);
        Assert.Equal((2, "id"), (error.RowIndex, error.Key));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Save_UnknownProviderKeptOnlyIfPreviouslyStored()
    {
        var goneId = Guid.NewGuid();
        Assert.Empty(await CreateService(includeGone: true).SaveUserLayoutAsync(
            _user,
            [new HomeRowInstance(goneId, Gone, null, new JsonObject { ["a"] = 1 }, true)],
            Ct));
        var service = CreateService();

        // Same id and provider: accepted, and the stored config is kept as sent.
        Assert.Empty(await service.SaveUserLayoutAsync(
            _user,
            [new HomeRowInstance(goneId, Gone, "Renamed", new JsonObject { ["b"] = 2 }, false), Row(HomeRowProviderIds.Resume)],
            Ct));
        var row = (await service.GetEffectiveLayoutAsync(_user, Ct)).Rows[0];
        Assert.False(row.IsAvailable);
        Assert.Equal("{\"b\":2}", row.Row.Config.ToJsonString());

        // A new id, or the stored id with another unknown provider, is rejected.
        var errors = await service.SaveUserLayoutAsync(
            _user,
            [Row(Gone), new HomeRowInstance(goneId, "other-gone", null, new JsonObject(), true)],
            Ct);
        Assert.Equal(new[] { (0, "provider"), (1, "provider") }, errors.Select(e => (e.RowIndex, e.Key)));

        // Another user's stored row does not count.
        Assert.Equal(
            "provider",
            Assert.Single(await service.SaveUserLayoutAsync(_other, [new HomeRowInstance(goneId, Gone, null, new JsonObject(), true)], Ct)).Key);
    }

    [Fact]
    public async Task Reset_ReturnsToInherited()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.NextUp)], Ct));
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.Resume)], Ct));
        Assert.Empty(await service.SaveUserLayoutAsync(_other, [Row(HomeRowProviderIds.Resume)], Ct));

        await service.ResetUserLayoutAsync(_user, Ct);
        await service.ResetUserLayoutAsync(_user, Ct);

        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        Assert.Equal(HomeRowLayoutSource.AdminDefault, layout.Source);
        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(HomeRowLayoutSource.User, (await service.GetEffectiveLayoutAsync(_other, Ct)).Source);
    }

    [Fact]
    public async Task Read_UnknownProvider_MarkedUnavailableNotRemoved()
    {
        Assert.Empty(await CreateService(includeGone: true).SaveUserLayoutAsync(
            _user,
            [Row(HomeRowProviderIds.Resume), new HomeRowInstance(Guid.NewGuid(), Gone, "Old", new JsonObject { ["a"] = 1 }, true)],
            Ct));

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(new[] { true, false }, layout.Rows.Select(r => r.IsAvailable));
        Assert.Equal(Gone, layout.Rows[1].Row.Provider);
        Assert.Equal("Old", layout.Rows[1].Row.Title);
        Assert.Equal("{\"a\":1}", layout.Rows[1].Row.Config.ToJsonString());
    }

    [Fact]
    public async Task Read_StoredValueNoLongerInSchema_StillLoads()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.LatestMedia, new JsonObject { ["library"] = "removed" })], Ct));

        var row = Assert.Single((await service.GetEffectiveLayoutAsync(_user, Ct)).Rows);

        Assert.True(row.IsAvailable);
        Assert.Equal("removed", (string?)row.Row.Config["library"]);
    }

    [Fact]
    public async Task FindRow_OtherUsersRowId_ReturnsNull()
    {
        var service = CreateService();
        var id = Guid.NewGuid();
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.Resume) with { Id = id }], Ct));

        Assert.Null(await service.FindRowAsync(_other, id, Ct));
        Assert.Equal(HomeRowProviderIds.Resume, (await service.FindRowAsync(_user, id, Ct))?.Row.Provider);
        Assert.Null(await service.FindRowAsync(_user, Guid.NewGuid(), Ct));

        var inherited = (await service.GetEffectiveLayoutAsync(_other, Ct)).Rows[1].Row;
        Assert.Equal(HomeRowProviderIds.Resume, (await service.FindRowAsync(_other, inherited.Id, Ct))?.Row.Provider);
    }

    [Fact]
    public async Task Read_NeverWritesDisplayPreferences()
    {
        GetPreferences(_user.Id, "usersettings".GetMD5(), "emby").HomeSections.Add(Section(1, 0, HomeSectionType.Resume));
        var service = CreateService();

        await service.GetEffectiveLayoutAsync(_user, Ct);
        await service.GetEffectiveLayoutAsync(_other, Ct);
        await service.FindRowAsync(_user, Guid.NewGuid(), Ct);

        _displayPreferences.Verify(m => m.GetDisplayPreferences(_user.Id, "usersettings".GetMD5(), "emby"), Times.Exactly(2));
        _displayPreferences.Verify(m => m.GetDisplayPreferences(_other.Id, "usersettings".GetMD5(), "emby"), Times.Once);
        _displayPreferences.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LatestViews_ExcludesPreferenceAndNonMediaViews()
    {
        var excluded = Guid.NewGuid();
        var kept = Guid.NewGuid();
        _user.SetPreference(PreferenceKind.LatestItemExcludes, [excluded.ToString("N")]);
        _userViews
            .Setup(m => m.GetUserViews(It.Is<UserViewQuery>(q => q.User == _user)))
            .Returns(
            [
                new CollectionFolder { Id = kept, CollectionType = CollectionType.movies },
                new CollectionFolder { Id = excluded, CollectionType = CollectionType.tvshows },
                new CollectionFolder { Id = Guid.NewGuid(), CollectionType = CollectionType.boxsets },
                new CollectionFolder { Id = Guid.NewGuid(), CollectionType = CollectionType.playlists },
                new UserView { Id = Guid.NewGuid(), ViewType = CollectionType.livetv },
                new MediaBrowser.Controller.Channels.Channel { Id = Guid.NewGuid() },
                new UserView { Id = Guid.NewGuid(), ViewType = CollectionType.folders },
            ]);

        var views = LatestViews.Get(_user, _userViews.Object);

        Assert.Equal(2, views.Count);
        Assert.True(views[0].Id.Equals(kept));
        Assert.Equal(CollectionType.folders, ((UserView)views[1]).ViewType);
    }

    private static string Library(Guid id) => id.ToString("N");

    private static HomeRowInstance Row(string provider, JsonObject? config = null)
        => new(Guid.NewGuid(), provider, null, config ?? new JsonObject(), true);

    private static IHomeRowProvider Provider(string id, params HomeRowSetting[] settings)
    {
        var provider = new Mock<IHomeRowProvider>();
        provider.SetupGet(p => p.Id).Returns(id);
        provider
            .Setup(p => p.GetSchemaAsync(It.IsAny<HomeRowContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HomeRowSchema(settings));
        return provider.Object;
    }

    private HomeRowConfigurationService CreateService(bool includeGone = false)
    {
        var providers = new List<IHomeRowProvider>
        {
            Provider(
                HomeRowProviderIds.Resume,
                new HomeRowSetting("limit", HomeRowSettingType.Integer, "Limit") { Default = 10, Minimum = 1, Maximum = 50 }),
            Provider(HomeRowProviderIds.NextUp),
            Provider(HomeRowProviderIds.LatestMedia, new HomeRowSetting("library", HomeRowSettingType.String, "Library")),
        };
        if (includeGone)
        {
            providers.Add(Provider(Gone, new HomeRowSetting("a", HomeRowSettingType.Integer, "A") { Default = 0 }));
        }

        return new HomeRowConfigurationService(
            _dbFactory,
            new HomeRowProviderFactory(providers),
            _displayPreferences.Object,
            _userViews.Object);
    }

    private DisplayPreferences GetPreferences(Guid userId, Guid itemId, string client)
    {
        var key = $"{userId}:{itemId}:{client}".GetMD5();
        if (!_preferences.TryGetValue(key, out var preferences))
        {
            preferences = new DisplayPreferences(userId, itemId, client);
            _preferences[key] = preferences;
        }

        return preferences;
    }

    private async Task AssertNothingStoredAsync()
    {
        await using var context = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Empty(await context.HomeRowConfigurations.ToListAsync(Ct));
    }

    private sealed class TestDbFactory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }
}
