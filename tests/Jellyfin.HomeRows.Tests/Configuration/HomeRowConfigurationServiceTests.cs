using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Schema;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.HomeRows.Tests.Configuration.UpstreamHomeSectionMapperTests;

namespace Jellyfin.HomeRows.Tests.Configuration;

public sealed class HomeRowConfigurationServiceTests : IDisposable
{
    private const string Gone = "gone-provider";
    private const string Expanding = "expanding";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"homerows-{Guid.NewGuid():N}.db");
    private readonly string _jellyfinDbPath = Path.Combine(Path.GetTempPath(), $"homerows-jellyfin-{Guid.NewGuid():N}.db");
    private readonly IDbContextFactory<CustomDbContext> _dbFactory;
    private readonly IDbContextFactory<JellyfinDbContext> _jellyfinDbFactory;
    private readonly Mock<IUserViewManager> _userViews = new();
    private readonly User _user = new("alice", "Default", "Default");
    private readonly User _other = new("bob", "Default", "Default");
    private readonly User _admin = new("admin", "Default", "Default");
    private readonly Guid _movies = Guid.NewGuid();
    private readonly Guid _shows = Guid.NewGuid();
    private readonly FakeExpandingProvider _expanding = new(Expanding);

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

        var jellyfinOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={_jellyfinDbPath}").Options;
        _jellyfinDbFactory = new TestJellyfinDbFactory(jellyfinOptions);
        using (var jellyfin = _jellyfinDbFactory.CreateDbContext())
        {
            jellyfin.Database.EnsureCreated();
            jellyfin.Users.AddRange(
                new User(_user.Username, "Default", "Default") { Id = _user.Id },
                new User(_other.Username, "Default", "Default") { Id = _other.Id },
                new User(_admin.Username, "Default", "Default") { Id = _admin.Id });
            jellyfin.SaveChanges();
        }

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
        File.Delete(_jellyfinDbPath);
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
                HomeRowProviderIds.UserList,
            },
            layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(new[] { Library(_movies), Library(_shows) }, layout.Rows.Skip(6).Take(2).Select(r => (string?)r.Row.Config["library"]));
        Assert.True(layout.Rows[1].IsAvailable);
        Assert.False(layout.Rows[0].IsAvailable);
    }

    [Fact]
    public async Task Effective_OnlyNoneSections_StandardDefault()
    {
        AddSections(_user.Id, "emby", Section(1, 0, HomeSectionType.None));

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.StandardDefault, layout.Source);
    }

    [Fact]
    public async Task Effective_UpstreamSections_UsedWhenNoAdminDefault()
    {
        AddSections(
            _user.Id,
            "emby",
            Section(1, 1, HomeSectionType.Resume),
            Section(2, 0, HomeSectionType.LatestMedia),
            Section(3, 2, HomeSectionType.None));
        AddSections(_user.Id, "other-client", Section(4, 0, HomeSectionType.NextUp));
        AddSections(_other.Id, "emby", Section(5, 0, HomeSectionType.NextUp));

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.UpstreamSections, layout.Source);
        Assert.Equal(
            new[] { HomeRowProviderIds.LatestMedia, HomeRowProviderIds.LatestMedia, HomeRowProviderIds.Resume, HomeRowProviderIds.UserList },
            layout.Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Effective_AdminDefault_BeatsUpstreamSections()
    {
        AddSections(_user.Id, "emby", Section(1, 0, HomeSectionType.Resume));
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
    public async Task UserLayouts_EveryUsersOwnStoredLayout_NotTheDefault()
    {
        var service = CreateService();
        Assert.Empty(await service.GetUserLayoutsAsync(Ct));

        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.NextUp)], Ct));
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.Resume)], Ct));
        Assert.Empty(await service.SaveUserLayoutAsync(_other, [Row(HomeRowProviderIds.Resume), Row(HomeRowProviderIds.NextUp)], Ct));

        var layouts = await service.GetUserLayoutsAsync(Ct);

        Assert.Equal(
            new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.Resume + "," + HomeRowProviderIds.NextUp }.Order(StringComparer.Ordinal),
            layouts.Select(l => string.Join(',', l.Select(r => r.Provider))).Order(StringComparer.Ordinal));
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
    public async Task ResetAdminDefault_RemovesItAndFallsBackForInheritingUsers()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.NextUp)], Ct));
        Assert.Empty(await service.SaveUserLayoutAsync(_other, [Row(HomeRowProviderIds.Resume)], Ct));
        Assert.Equal(HomeRowLayoutSource.AdminDefault, (await service.GetEffectiveLayoutAsync(_user, Ct)).Source);

        await service.ResetAdminDefaultAsync(Ct);
        await service.ResetAdminDefaultAsync(Ct);

        Assert.Null(await service.GetAdminDefaultAsync(Ct));
        Assert.NotEqual(HomeRowLayoutSource.AdminDefault, (await service.GetEffectiveLayoutAsync(_user, Ct)).Source);
        Assert.Equal(HomeRowLayoutSource.User, (await service.GetEffectiveLayoutAsync(_other, Ct)).Source);
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.Resume)], Ct));
        Assert.Single(await service.GetAdminDefaultAsync(Ct) ?? []);
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
        AddSections(_user.Id, "emby", Section(1, 0, HomeSectionType.Resume));
        var service = CreateService();
        var before = await CountPreferencesAsync();

        var withSections = await service.GetEffectiveLayoutAsync(_user, Ct);
        var withoutRow = await service.GetEffectiveLayoutAsync(_other, Ct);
        await service.FindRowAsync(_admin, Guid.NewGuid(), Ct);

        Assert.Equal(HomeRowLayoutSource.UpstreamSections, withSections.Source);
        Assert.Equal(new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.UserList }, withSections.Rows.Select(r => r.Row.Provider));
        Assert.Equal(HomeRowLayoutSource.StandardDefault, withoutRow.Source);
        Assert.Equal((1, 1), before);
        Assert.Equal(before, await CountPreferencesAsync());
    }

    [Fact]
    public async Task Save_InheritingUser_CanResaveUnavailableAdminDefaultRow()
    {
        var goneId = Guid.NewGuid();
        Assert.Empty(await CreateService(includeGone: true).SaveAdminDefaultAsync(
            _admin,
            [new HomeRowInstance(goneId, Gone, null, new JsonObject { ["a"] = 1 }, true)],
            Ct));
        var service = CreateService();
        var shown = Assert.Single((await service.GetEffectiveLayoutAsync(_user, Ct)).Rows);
        Assert.False(shown.IsAvailable);

        Assert.Equal(
            "provider",
            Assert.Single(await service.SaveUserLayoutAsync(_user, [shown.Row, Row(Gone)], Ct)).Key);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [shown.Row, Row(HomeRowProviderIds.Resume)], Ct));

        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        Assert.Equal(HomeRowLayoutSource.User, layout.Source);
        Assert.True(layout.Rows[0].Row.Id.Equals(goneId));
        Assert.Equal("{\"a\":1}", layout.Rows[0].Row.Config.ToJsonString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"}]")]
    [InlineData("[{\"provider\":\"resume\"}]")]
    [InlineData("[{\"id\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\",\"provider\":\"resume\",\"config\":[1]}]")]
    public async Task Read_CorruptRecords_FallThrough(string json)
    {
        AddSections(_other.Id, "emby", Section(1, 0, HomeSectionType.NextUp));
        var service = CreateService();
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.Resume)], Ct));
        await StoreUserJsonAsync(_user.Id, json);

        // A corrupt own record falls through to the admin default.
        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        Assert.Equal(HomeRowLayoutSource.AdminDefault, layout.Source);
        Assert.Equal(new[] { HomeRowProviderIds.Resume }, layout.Rows.Select(r => r.Row.Provider));

        // A corrupt admin default falls through to upstream sections, or the standard order.
        await StoreDefaultJsonAsync(json);
        Assert.Null(await service.GetAdminDefaultAsync(Ct));
        Assert.Equal(HomeRowLayoutSource.StandardDefault, (await service.GetEffectiveLayoutAsync(_user, Ct)).Source);
        Assert.Equal(HomeRowLayoutSource.UpstreamSections, (await service.GetEffectiveLayoutAsync(_other, Ct)).Source);
        Assert.Equal(HomeRowLayoutSource.StandardDefault, (await service.GetEffectiveLayoutAsync(_admin, Ct)).Source);
    }

    [Fact]
    public async Task Save_CorruptStoredRecords_AreOverwritten()
    {
        var service = CreateService();
        await StoreUserJsonAsync(_user.Id, "not json");
        await StoreDefaultJsonAsync("not json");

        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(HomeRowProviderIds.NextUp)], Ct));
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [Row(HomeRowProviderIds.Resume)], Ct));
        Assert.Equal("provider", Assert.Single(await service.SaveUserLayoutAsync(_user, [Row(Gone)], Ct)).Key);

        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        Assert.Equal(HomeRowLayoutSource.User, layout.Source);
        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(new[] { HomeRowProviderIds.Resume }, (await service.GetAdminDefaultAsync(Ct))!.Select(r => r.Provider));
    }

    [Fact]
    public async Task Read_MissingEnabledAndConfig_DefaultToEnabledAndEmpty()
    {
        var id = Guid.NewGuid();
        await StoreUserJsonAsync(_user.Id, $"[{{\"id\":\"{id}\",\"provider\":\"resume\"}}]");

        var row = Assert.Single((await CreateService().GetEffectiveLayoutAsync(_user, Ct)).Rows).Row;

        Assert.True(row.Id.Equals(id));
        Assert.True(row.Enabled);
        Assert.Null(row.Title);
        Assert.Empty(row.Config);
    }

    [Fact]
    public async Task Save_NullConfig_TreatedAsEmpty()
    {
        var service = CreateService();

        Assert.Empty(await service.SaveUserLayoutAsync(_user, [new HomeRowInstance(Guid.Empty, HomeRowProviderIds.Resume, null, null!, true)], Ct));

        var row = Assert.Single((await service.GetEffectiveLayoutAsync(_user, Ct)).Rows).Row;
        Assert.Equal("{\"limit\":10}", row.Config.ToJsonString());
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

    [Fact]
    public async Task Expand_True_ThreeListsMaxTwo_TwoRowsWithDistinctStableIds()
    {
        var service = CreateService();
        var parent = Row(Expanding, new JsonObject { ["maxRows"] = 2 });
        var next = Row(HomeRowProviderIds.NextUp);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent, next], Ct));
        _expanding.Lists = 3;

        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        var expanded = await service.ExpandAsync(_user, layout, Ct);
        var again = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        Assert.Equal(2, layout.Rows.Count);
        Assert.Equal(layout.Source, expanded.Source);
        Assert.Equal(new[] { Expanding, Expanding, HomeRowProviderIds.NextUp }, expanded.Rows.Select(r => r.Row.Provider));
        var derived = expanded.Rows.Take(2).ToArray();
        Assert.False(derived[0].Row.Id.Equals(derived[1].Row.Id));
        Assert.All(derived, d => Assert.False(d.Row.Id.Equals(parent.Id)));
        Assert.Equal(new[] { "list-0", "list-1" }, derived.Select(d => d.SubRowKey));
        Assert.Equal(
            new[] { HomeRowConfigurationService.GetDerivedRowId(parent.Id, "list-0"), HomeRowConfigurationService.GetDerivedRowId(parent.Id, "list-1") },
            derived.Select(d => d.Row.Id));
        Assert.Equal(derived.Select(d => d.Row.Id), again.Rows.Take(2).Select(r => r.Row.Id));
        Assert.All(derived, d =>
        {
            Assert.True(d.Row.Enabled);
            Assert.True(d.IsAvailable);
            Assert.Equal(2, (int)d.Row.Config["maxRows"]!);
        });
        Assert.Equal(next.Id, expanded.Rows[2].Row.Id);

        // The provider got the validated config, with the schema's defaults filled in.
        Assert.Equal(2, (int)_expanding.LastConfig!["maxRows"]!);
        Assert.Equal("poster", (string?)_expanding.LastConfig["cardStyle"]);
    }

    [Fact]
    public async Task Expand_ParentWithCustomTitle_DerivedRowsHaveNoTitle()
    {
        var service = CreateService();
        var parent = Row(Expanding) with { Title = "My title" };
        var plain = Row(HomeRowProviderIds.NextUp) with { Title = "Keep me" };
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent, plain], Ct));
        _expanding.Lists = 2;

        var layout = await service.GetEffectiveLayoutAsync(_user, Ct);
        var expanded = await service.ExpandAsync(_user, layout, Ct);

        Assert.Equal("My title", layout.Rows[0].Row.Title);
        Assert.Equal(3, expanded.Rows.Count);
        Assert.Null(expanded.Rows[0].Row.Title);
        Assert.Null(expanded.Rows[1].Row.Title);
        Assert.Equal("Keep me", expanded.Rows[2].Row.Title);
    }

    [Fact]
    public void DerivedRowId_IsParentPrefixPlusKeyHash()
    {
        var parent = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var other = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        // Not security related: the expected value of the id derivation.
#pragma warning disable CA5351
        var prefix = System.Security.Cryptography.MD5.HashData(parent.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes("derived-row")).ToArray());
        var keyHash = System.Security.Cryptography.MD5.HashData(parent.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes("romance-new")).ToArray());
#pragma warning restore CA5351
        var expected = new Guid(prefix.Take(4).Concat(keyHash.Skip(4)).ToArray());

        Assert.Equal(expected, HomeRowConfigurationService.GetDerivedRowId(parent, "romance-new"));
        Assert.True(HomeRowConfigurationService.HasDerivedRowPrefix(parent, HomeRowConfigurationService.GetDerivedRowId(parent, "pixar")));
        Assert.False(HomeRowConfigurationService.HasDerivedRowPrefix(other, HomeRowConfigurationService.GetDerivedRowId(parent, "pixar")));
        Assert.False(HomeRowConfigurationService.HasDerivedRowPrefix(parent, parent));
        Assert.Equal(HomeRowConfigurationService.GetDerivedRowId(parent, "romance-new"), HomeRowConfigurationService.GetDerivedRowId(parent, "romance-new"));
        Assert.NotEqual(HomeRowConfigurationService.GetDerivedRowId(parent, "romance-new"), HomeRowConfigurationService.GetDerivedRowId(parent, "pixar"));
        Assert.NotEqual(HomeRowConfigurationService.GetDerivedRowId(parent, "romance-new"), HomeRowConfigurationService.GetDerivedRowId(other, "romance-new"));
        Assert.NotEqual(parent, HomeRowConfigurationService.GetDerivedRowId(parent, string.Empty));
    }

    [Fact]
    public async Task Expand_UsesSubRowKeys_InProviderOrder()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Keys = ["pixar", "romance-new", "noir"];

        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        Assert.Equal(new[] { "pixar", "romance-new", "noir" }, expanded.Rows.Select(r => r.SubRowKey));
        Assert.Equal(
            new[] { "pixar", "romance-new", "noir" }.Select(k => HomeRowConfigurationService.GetDerivedRowId(parent.Id, k)),
            expanded.Rows.Select(r => r.Row.Id));
    }

    [Fact]
    public async Task Expand_NoLists_RowRemoved()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(Expanding), Row(HomeRowProviderIds.NextUp)], Ct));
        _expanding.Lists = 0;

        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, expanded.Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Expand_ProviderFails_RowRemoved()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(Expanding), Row(HomeRowProviderIds.NextUp)], Ct));
        _expanding.Fail = true;

        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        Assert.Equal(new[] { HomeRowProviderIds.NextUp }, expanded.Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Expand_DisabledRow_KeptAsIs()
    {
        var service = CreateService();
        var disabled = Row(Expanding) with { Enabled = false };
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [disabled], Ct));
        _expanding.Lists = 3;

        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        var row = Assert.Single(expanded.Rows);
        Assert.Equal(disabled.Id, row.Row.Id);
        Assert.False(row.Row.Enabled);
        Assert.Equal(0, _expanding.CountCalls);
    }

    [Fact]
    public async Task Expand_CountAboveMaximum_Capped()
    {
        var service = CreateService();
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [Row(Expanding, new JsonObject { ["maxRows"] = 10 })], Ct));
        _expanding.Lists = 1000;
        _expanding.IgnoreMaxRows = true;

        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        Assert.Equal(HomeRowConfigurationService.MaxExpandedRows, expanded.Rows.Count);
    }

    [Fact]
    public async Task FindRow_DerivedId_ReturnsParentWithSubRowKey()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Lists = 3;
        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        var found = await service.FindRowAsync(_user, expanded.Rows[2].Row.Id, Ct);
        var direct = await service.FindRowAsync(_user, parent.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(parent.Id, found.Row.Id);
        Assert.Equal("list-2", found.SubRowKey);
        Assert.NotNull(direct);
        Assert.Null(direct.SubRowKey);
        Assert.Null(await service.FindRowAsync(_other, expanded.Rows[2].Row.Id, Ct));
    }

    [Fact]
    public async Task FindRow_KeyNoLongerPickedButResolvable_ReturnsIt()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Keys = ["romance-new", "pixar"];
        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);
        var romance = expanded.Rows[0].Row.Id;

        // The picks change (a re-pick, a list replaced, midnight): romance is no longer shown, but still in the store.
        _expanding.Keys = ["pixar", "noir"];
        _expanding.ExtraResolvable = ["romance-new"];

        var found = await service.FindRowAsync(_user, romance, Ct);

        Assert.NotNull(found);
        Assert.Equal(parent.Id, found.Row.Id);
        Assert.Equal("romance-new", found.SubRowKey);

        // Deleted from the store: the id still carries the parent's prefix, so it is the parent with the unknown key.
        _expanding.ExtraResolvable = [];
        var gone = await service.FindRowAsync(_user, romance, Ct);
        Assert.NotNull(gone);
        Assert.Equal(parent.Id, gone.Row.Id);
        Assert.Equal(HomeSubRow.UnknownKey, gone.SubRowKey);
    }

    [Fact]
    public async Task FindRow_IdWithoutParentPrefix_ReturnsNull()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Keys = ["a"];

        // A random id, and an index-based id from before keys (MD5 of the parent bytes and the int 0).
        var bytes = parent.Id.ToByteArray().Concat(BitConverter.GetBytes(0)).ToArray();
#pragma warning disable CA5351
        var oldIndexId = new Guid(System.Security.Cryptography.MD5.HashData(bytes));
#pragma warning restore CA5351

        Assert.Null(await service.FindRowAsync(_user, Guid.NewGuid(), Ct));
        Assert.Null(await service.FindRowAsync(_user, oldIndexId, Ct));
        Assert.Equal(0, _expanding.ResolvableCalls);
    }

    [Fact]
    public async Task FindRow_ResolvableKeysFail_DerivedIdThrows_ParentStillFound()
    {
        // A failing provider is an error (500), not a silently empty row that looks like a deleted list.
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Keys = ["a"];
        _expanding.FailResolvable = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FindRowAsync(_user, HomeRowConfigurationService.GetDerivedRowId(parent.Id, "a"), Ct));
        Assert.NotNull(await service.FindRowAsync(_user, parent.Id, Ct));
    }

    [Fact]
    public async Task Expand_SkipsEmptySubRowKeys()
    {
        // "" is HomeSubRow.UnknownKey, never a real key.
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Keys = [string.Empty, "a"];

        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        Assert.Equal(new[] { "a" }, expanded.Rows.Where(r => r.Row.Provider == Expanding).Select(r => r.SubRowKey));
    }

    [Fact]
    public void NewRowId_RedrawsAnIdThatCarriesAnExpandingRowsPrefix()
    {
        var parent = Guid.NewGuid();
        var clash = HomeRowConfigurationService.GetDerivedRowId(parent, "x");
        var fine = Guid.NewGuid();
        var draws = new Queue<Guid>([clash, fine]);

        Assert.Equal(fine, HomeRowConfigurationService.NewRowId([parent], draws.Dequeue));
        Assert.Empty(draws);
    }

    [Fact]
    public async Task FindRow_DerivedIdOfDisabledRow_ReturnsNull()
    {
        var service = CreateService();
        var parent = Row(Expanding) with { Enabled = false };
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));

        _expanding.Keys = ["list-0"];

        Assert.Null(await service.FindRowAsync(_user, HomeRowConfigurationService.GetDerivedRowId(parent.Id, "list-0"), Ct));
    }

    [Fact]
    public async Task Save_WithExpandedIds_IsRejected()
    {
        // Ruling: a save that echoes the expanded (display) layout is rejected, so it can't turn one discovery row into
        // several. Clients edit the layout from GET Configuration without expand.
        var service = CreateService();
        var parent = Row(Expanding, new JsonObject { ["maxRows"] = 2 });
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Lists = 2;
        var expanded = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);

        var errors = await service.SaveUserLayoutAsync(_user, expanded.Rows.Select(r => r.Row).ToArray(), Ct);
        var mixed = await service.SaveUserLayoutAsync(_user, [parent, expanded.Rows[1].Row], Ct);

        Assert.Equal(new[] { 0, 1 }, errors.Select(e => e.RowIndex));
        Assert.All(errors, e => Assert.Equal("id", e.Key));
        Assert.Equal(1, Assert.Single(mixed).RowIndex);
        var stored = Assert.Single((await service.GetEffectiveLayoutAsync(_user, Ct)).Rows);
        Assert.Equal(parent.Id, stored.Row.Id);
    }

    [Fact]
    public async Task Save_AdminDefaultWithExpandedIds_IsRejected()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [parent], Ct));

        _expanding.Keys = ["list-0"];

        var errors = await service.SaveAdminDefaultAsync(_admin, [Row(Expanding) with { Id = HomeRowConfigurationService.GetDerivedRowId(parent.Id, "list-0") }], Ct);

        Assert.Equal("id", Assert.Single(errors).Key);
    }

    [Fact]
    public async Task Save_IdWithExpandingParentPrefix_IsRejected()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.Keys = [];

        // Any key, even one the provider never had: the parent's prefix is enough.
        var errors = await service.SaveUserLayoutAsync(_user, [parent, Row(HomeRowProviderIds.NextUp) with { Id = HomeRowConfigurationService.GetDerivedRowId(parent.Id, "never-a-list") }], Ct);

        var error = Assert.Single(errors);
        Assert.Equal(1, error.RowIndex);
        Assert.Equal("id", error.Key);
    }

    [Fact]
    public async Task Save_ResolvableKeysFail_LayoutStillSaved()
    {
        var service = CreateService();
        var parent = Row(Expanding);
        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent], Ct));
        _expanding.FailResolvable = true;
        var next = Row(HomeRowProviderIds.NextUp);

        Assert.Empty(await service.SaveUserLayoutAsync(_user, [parent, next], Ct));
        Assert.Empty(await service.SaveAdminDefaultAsync(_admin, [parent, next], Ct));

        Assert.Equal(new[] { parent.Id, next.Id }, (await service.GetEffectiveLayoutAsync(_user, Ct)).Rows.Select(r => r.Row.Id));
    }

    [Fact]
    public async Task DefaultLayout_HasNoMovieDiscovery()
    {
        var service = CreateService();
        _expanding.Lists = 3;

        var standard = await service.ExpandAsync(_user, await service.GetEffectiveLayoutAsync(_user, Ct), Ct);
        AddSections(_other.Id, "emby", Enum.GetValues<HomeSectionType>().Select((t, i) => Section(i + 1, i, t)).ToArray());
        var upstream = await service.ExpandAsync(_other, await service.GetEffectiveLayoutAsync(_other, Ct), Ct);

        Assert.Equal(HomeRowLayoutSource.StandardDefault, standard.Source);
        Assert.Equal(HomeRowLayoutSource.UpstreamSections, upstream.Source);
        Assert.DoesNotContain(standard.Rows, r => r.Row.Provider == Expanding || r.Row.Provider == HomeRowProviderIds.MovieDiscovery);
        Assert.DoesNotContain(upstream.Rows, r => r.Row.Provider == Expanding || r.Row.Provider == HomeRowProviderIds.MovieDiscovery);
    }

    [Fact]
    public async Task UserListRow_InsertedOnceAfterDiscovery()
    {
        // Alice has Movie discovery mid-layout, Bob has none, the admin default has it last; the admin's own layout
        // already has a user-list row.
        var discovery = Guid.NewGuid();
        await StoreUserJsonAsync(_user.Id, Json(Stored(Guid.NewGuid(), HomeRowProviderIds.Resume), Stored(discovery, HomeRowProviderIds.MovieDiscovery), Stored(Guid.NewGuid(), HomeRowProviderIds.NextUp)));
        await StoreUserJsonAsync(_other.Id, Json(Stored(Guid.NewGuid(), HomeRowProviderIds.Resume)));
        await StoreUserJsonAsync(_admin.Id, Json(Stored(Guid.NewGuid(), HomeRowProviderIds.UserList), Stored(Guid.NewGuid(), HomeRowProviderIds.MovieDiscovery)));
        await StoreDefaultJsonAsync(Json(Stored(Guid.NewGuid(), HomeRowProviderIds.MovieDiscovery)));
        var service = CreateService();

        Assert.True(await service.InsertUserListRowsOnceAsync(Ct));

        var alice = (await service.GetEffectiveLayoutAsync(_user, Ct)).Rows.Select(r => r.Row).ToList();
        Assert.Equal(
            new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.MovieDiscovery, HomeRowProviderIds.UserList, HomeRowProviderIds.NextUp },
            alice.Select(r => r.Provider));
        var inserted = alice[2];
        Assert.True(inserted.Enabled);
        Assert.Null(inserted.Title);
        Assert.Empty(inserted.Config);
        Assert.False(HomeRowConfigurationService.HasDerivedRowPrefix(discovery, inserted.Id));
        Assert.Equal(
            new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.UserList },
            (await service.GetEffectiveLayoutAsync(_other, Ct)).Rows.Select(r => r.Row.Provider));
        Assert.Equal(
            new[] { HomeRowProviderIds.UserList, HomeRowProviderIds.MovieDiscovery },
            (await service.GetEffectiveLayoutAsync(_admin, Ct)).Rows.Select(r => r.Row.Provider));
        Assert.Equal(
            new[] { HomeRowProviderIds.MovieDiscovery, HomeRowProviderIds.UserList },
            (await service.GetAdminDefaultAsync(Ct))!.Select(r => r.Provider));

        // Alice removes the row; the insert never runs again, on this service or a new one (a restart).
        await StoreUserJsonAsync(_user.Id, Json(Stored(Guid.NewGuid(), HomeRowProviderIds.Resume)));
        Assert.False(await service.InsertUserListRowsOnceAsync(Ct));
        Assert.False(await CreateService().InsertUserListRowsOnceAsync(Ct));
        Assert.Equal(
            new[] { HomeRowProviderIds.Resume },
            (await service.GetEffectiveLayoutAsync(_user, Ct)).Rows.Select(r => r.Row.Provider));
    }

    [Fact]
    public async Task Effective_StandardDefault_EndsWithSavedRow_NothingStored()
    {
        var service = CreateService();

        var first = await service.GetEffectiveLayoutAsync(_user, Ct);
        var second = await service.GetEffectiveLayoutAsync(_other, Ct);

        Assert.Equal(HomeRowLayoutSource.StandardDefault, first.Source);
        var saved = first.Rows[^1].Row;
        Assert.Equal(HomeRowProviderIds.UserList, saved.Provider);
        Assert.True(saved.Enabled);
        Assert.Empty(saved.Config);
        Assert.Equal(saved.Id, second.Rows[^1].Row.Id);
        Assert.Single(first.Rows, r => r.Row.Provider == HomeRowProviderIds.UserList);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Effective_UpstreamSections_SavedRowAtReadTimeOnly()
    {
        AddSections(_user.Id, "emby", Section(1, 0, HomeSectionType.Resume), Section(2, 1, HomeSectionType.NextUp));
        var before = await CountPreferencesAsync();

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(HomeRowLayoutSource.UpstreamSections, layout.Source);
        Assert.Equal(
            new[] { HomeRowProviderIds.Resume, HomeRowProviderIds.NextUp, HomeRowProviderIds.UserList },
            layout.Rows.Select(r => r.Row.Provider));
        Assert.Equal(before, await CountPreferencesAsync());
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Effective_UpstreamSections_Full_NoSavedRow()
    {
        AddSections(_user.Id, "emby", Enumerable.Range(0, UpstreamHomeSectionMapper.MaxRows).Select(i => Section(i + 1, i, HomeSectionType.Resume)).ToArray());

        var layout = await CreateService().GetEffectiveLayoutAsync(_user, Ct);

        Assert.Equal(UpstreamHomeSectionMapper.MaxRows, layout.Rows.Count);
        Assert.DoesNotContain(layout.Rows, r => r.Row.Provider == HomeRowProviderIds.UserList);
    }

    [Fact]
    public async Task UserListRow_FullStoredLayout_SkippedAndLogged()
    {
        var full = Enumerable.Range(0, UpstreamHomeSectionMapper.MaxRows).Select(_ => Stored(Guid.NewGuid(), HomeRowProviderIds.NextUp)).ToArray();
        await StoreUserJsonAsync(_user.Id, Json(full));
        var logger = new ListLogger();

        Assert.True(await CreateService(logger: logger).InsertUserListRowsOnceAsync(Ct));

        Assert.DoesNotContain((await CreateService().GetEffectiveLayoutAsync(_user, Ct)).Rows, r => r.Row.Provider == HomeRowProviderIds.UserList);
        Assert.Contains(logger.Messages, m => m.Level == LogLevel.Warning && m.Text.Contains(_user.Id.ToString(), StringComparison.Ordinal));
    }

    private static JsonObject Stored(Guid id, string provider)
        => new() { ["id"] = id.ToString("D"), ["provider"] = provider, ["title"] = null, ["config"] = new JsonObject(), ["enabled"] = true };

    private static string Json(params JsonObject[] rows)
        => new JsonArray(rows.Select(r => (JsonNode)r).ToArray()).ToJsonString();

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

    private HomeRowConfigurationService CreateService(bool includeGone = false, ILogger<HomeRowConfigurationService>? logger = null)
    {
        var providers = new List<IHomeRowProvider>
        {
            _expanding,
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
            _jellyfinDbFactory,
            new HomeRowProviderFactory(providers),
            _userViews.Object,
            logger ?? NullLogger<HomeRowConfigurationService>.Instance);
    }

    private void AddSections(Guid userId, string client, params HomeSection[] sections)
    {
        using var context = _jellyfinDbFactory.CreateDbContext();
        var preferences = new DisplayPreferences(userId, "usersettings".GetMD5(), client);
        foreach (var section in sections)
        {
            preferences.HomeSections.Add(section);
        }

        context.DisplayPreferences.Add(preferences);
        context.SaveChanges();
    }

    private async Task<(int Preferences, int Sections)> CountPreferencesAsync()
    {
        await using var context = await _jellyfinDbFactory.CreateDbContextAsync(Ct);
        return (await context.DisplayPreferences.CountAsync(Ct), await context.Set<HomeSection>().CountAsync(Ct));
    }

    private async Task StoreUserJsonAsync(Guid userId, string json)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(Ct);
        await context.HomeRowConfigurations.Where(c => c.UserId.Equals(userId)).ExecuteDeleteAsync(Ct);
        context.HomeRowConfigurations.Add(new CustomHomeRowConfiguration { UserId = userId, RowsJson = json, DateModified = DateTime.UtcNow });
        await context.SaveChangesAsync(Ct);
    }

    private async Task StoreDefaultJsonAsync(string json)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(Ct);
        await context.HomeRowDefaults.ExecuteDeleteAsync(Ct);
        context.HomeRowDefaults.Add(new CustomHomeRowDefault { RowsJson = json, DateModified = DateTime.UtcNow });
        await context.SaveChangesAsync(Ct);
    }

    private async Task AssertNothingStoredAsync()
    {
        await using var context = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Empty(await context.HomeRowConfigurations.ToListAsync(Ct));
    }

    private sealed class FakeExpandingProvider(string id) : IExpandingHomeRowProvider
    {
        // Today's sub-row keys, in order; Lists sets them to list-0, list-1, ...
        public IReadOnlyList<string> Keys { get; set; } = [];

        public int Lists
        {
            get => Keys.Count;
            set => Keys = Enumerable.Range(0, value).Select(i => "list-" + i).ToArray();
        }

        // Keys the provider can still serve beyond today's.
        public IReadOnlyList<string> ExtraResolvable { get; set; } = [];

        public bool Fail { get; set; }

        public bool FailResolvable { get; set; }

        public int ResolvableCalls { get; private set; }

        public bool IgnoreMaxRows { get; set; }

        public int CountCalls { get; private set; }

        public JsonObject? LastConfig { get; private set; }

        public string Id => id;

        public string Name => "Expanding";

        public string Description => "Expands into one row per list.";

        public Task<HomeRowSchema> GetSchemaAsync(HomeRowContext context, CancellationToken cancellationToken)
            => Task.FromResult(new HomeRowSchema(
            [
                new HomeRowSetting("maxRows", HomeRowSettingType.Integer, "Rows") { Default = 3, Minimum = 1, Maximum = 10 },
                new HomeRowSetting("cardStyle", HomeRowSettingType.String, "Card style") { Default = "poster" }
            ]));

        public Task<IReadOnlyList<HomeSubRow>> GetSubRowsAsync(HomeRowContext context, JsonObject config, CancellationToken ct)
        {
            CountCalls++;
            LastConfig = config;
            if (Fail)
            {
                throw new InvalidOperationException("The lists are unavailable.");
            }

            var keys = IgnoreMaxRows ? Keys : Keys.Take((int)config["maxRows"]!);
            return Task.FromResult<IReadOnlyList<HomeSubRow>>(keys.Select(k => new HomeSubRow(k)).ToArray());
        }

        public Task<IReadOnlyList<string>> GetResolvableSubRowKeysAsync(HomeRowContext context, JsonObject config, CancellationToken ct)
        {
            ResolvableCalls++;
            if (Fail || FailResolvable)
            {
                throw new InvalidOperationException("The lists are unavailable.");
            }

            return Task.FromResult<IReadOnlyList<string>>(Keys.Concat(ExtraResolvable).ToArray());
        }

        public Task<HomeRowResult> GetItemsAsync(HomeRowContext context, JsonObject config, HomeRowPaging paging, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class TestDbFactory(DbContextOptions<CustomDbContext> options) : IDbContextFactory<CustomDbContext>
    {
        public CustomDbContext CreateDbContext() => new(options);
    }

    private sealed class TestJellyfinDbFactory(DbContextOptions<JellyfinDbContext> options) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => new(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new Mock<IJellyfinDatabaseProvider>().Object,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed class ListLogger : ILogger<HomeRowConfigurationService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue((logLevel, formatter(state, exception)));
    }
}
