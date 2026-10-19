using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Custom.Database;
using Jellyfin.Custom.Database.Entities;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.FullSystemBackup;
using Jellyfin.Server.Implementations.Tests.Item;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.FullSystemBackup;

public sealed class CustomDatabaseBackupContributorTests : SqliteDbTestFixture
{
    private const string ConfigurationsEntry = "Custom/CustomHomeRowConfigurations.json";
    private const string DefaultsEntry = "Custom/CustomHomeRowDefaults.json";

    private readonly SqliteConnection _customConnection;
    private readonly DbContextOptions<CustomDbContext> _customOptions;
    private readonly CustomDatabaseBackupContributor _contributor;

    public CustomDatabaseBackupContributorTests()
    {
        _customConnection = new SqliteConnection("Data Source=:memory:");
        _customConnection.Open();
        _customOptions = new DbContextOptionsBuilder<CustomDbContext>().UseSqlite(_customConnection).Options;
        using (var context = new CustomDbContext(_customOptions))
        {
            context.Database.EnsureCreated();
        }

        var customFactory = new Mock<IDbContextFactory<CustomDbContext>>();
        customFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CustomDbContext(_customOptions));
        _contributor = new CustomDatabaseBackupContributor(customFactory.Object, CreateDbContextFactory());
    }

    [Fact]
    public async Task Write_ThenRestore_RoundTripsBothTables()
    {
        var userId = Guid.NewGuid();
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await SeedAsync(
            [new CustomHomeRowConfiguration { UserId = userId, RowsJson = "[1]", DateModified = modified }],
            new CustomHomeRowDefault { RowsJson = "[2]", DateModified = modified, ModifiedByUserId = userId });

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            await _contributor.WriteAsync(archive, TestContext.Current.CancellationToken);
        }

        await SeedAsync([], null, clear: true);
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.NotNull(archive.GetEntry(ConfigurationsEntry));
            Assert.NotNull(archive.GetEntry(DefaultsEntry));
            await _contributor.RestoreAsync(archive, TestContext.Current.CancellationToken);
        }

        await using var context = new CustomDbContext(_customOptions);
        var config = Assert.Single(await context.HomeRowConfigurations.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(userId, config.UserId);
        Assert.Equal("[1]", config.RowsJson);
        Assert.Equal(modified, config.DateModified);
        var def = Assert.Single(await context.HomeRowDefaults.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("[2]", def.RowsJson);
        Assert.Equal(userId, def.ModifiedByUserId);
    }

    [Fact]
    public async Task Restore_WithoutCustomEntries_RemovesConfigurationsOfUnknownUsersAndKeepsDefault()
    {
        var userA = await AddUserAsync("a");
        var userB = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedAsync(
            [
                new CustomHomeRowConfiguration { UserId = userA, RowsJson = "[]", DateModified = now },
                new CustomHomeRowConfiguration { UserId = userB, RowsJson = "[]", DateModified = now }
            ],
            new CustomHomeRowDefault { RowsJson = "[3]", DateModified = now, ModifiedByUserId = userA });

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("manifest.json");
        }

        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            await _contributor.RestoreAsync(archive, TestContext.Current.CancellationToken);
        }

        await using var context = new CustomDbContext(_customOptions);
        Assert.Equal(userA, Assert.Single(await context.HomeRowConfigurations.ToListAsync(TestContext.Current.CancellationToken)).UserId);
        Assert.Equal("[3]", Assert.Single(await context.HomeRowDefaults.ToListAsync(TestContext.Current.CancellationToken)).RowsJson);
    }

    [Fact]
    public async Task Restore_WithEntries_ReplacesTables()
    {
        var oldUser = Guid.NewGuid();
        var newUser = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedAsync(
            [new CustomHomeRowConfiguration { UserId = oldUser, RowsJson = "old", DateModified = now }],
            new CustomHomeRowDefault { RowsJson = "old", DateModified = now, ModifiedByUserId = oldUser });

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            await WriteEntryAsync(archive, ConfigurationsEntry, new[] { new CustomHomeRowConfiguration { UserId = newUser, RowsJson = "new", DateModified = now } });
            await WriteEntryAsync(archive, DefaultsEntry, new[] { new CustomHomeRowDefault { RowsJson = "new", DateModified = now, ModifiedByUserId = newUser } });
        }

        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            await _contributor.RestoreAsync(archive, TestContext.Current.CancellationToken);
        }

        await using var context = new CustomDbContext(_customOptions);
        var config = Assert.Single(await context.HomeRowConfigurations.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(newUser, config.UserId);
        Assert.Equal("new", config.RowsJson);
        var def = Assert.Single(await context.HomeRowDefaults.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("new", def.RowsJson);
        Assert.Equal(newUser, def.ModifiedByUserId);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _customConnection.Dispose();
        }

        base.Dispose(disposing);
    }

    private static async Task WriteEntryAsync<T>(ZipArchive archive, string name, T value)
    {
        var stream = await archive.CreateEntry(name).OpenAsync(TestContext.Current.CancellationToken);
        await using (stream)
        {
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private async Task<Guid> AddUserAsync(string name)
    {
        await using var context = CreateDbContext();
        var user = new User(name, "provider", "reset");
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task SeedAsync(CustomHomeRowConfiguration[] configs, CustomHomeRowDefault? def, bool clear = false)
    {
        await using var context = new CustomDbContext(_customOptions);
        if (clear)
        {
            context.HomeRowConfigurations.RemoveRange(context.HomeRowConfigurations);
            context.HomeRowDefaults.RemoveRange(context.HomeRowDefaults);
        }

        context.HomeRowConfigurations.AddRange(configs);
        if (def is not null)
        {
            context.HomeRowDefaults.Add(def);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
