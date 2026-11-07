using System;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations.TV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.TV;

public sealed class RewatchMarkerStoreTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "rewatch-store-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();

    public RewatchMarkerStoreTests()
    {
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose() => Directory.Delete(_dataPath, true);

    private RewatchMarkerStore NewStore()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_dataPath);
        return new RewatchMarkerStore(paths.Object, NullLogger<RewatchMarkerStore>.Instance);
    }

    private static RewatchMarker Marker(string series = "svu") => new(series, Guid.NewGuid(), new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Set_ThenGet_ReturnsMarker()
    {
        var store = NewStore();
        var marker = Marker();
        store.Set(_user, marker);
        Assert.Equal(marker, store.Get(_user, "svu"));
    }

    [Fact]
    public void Set_SameSeries_Replaces()
    {
        var store = NewStore();
        store.Set(_user, Marker());
        var second = Marker();
        store.Set(_user, second);
        Assert.Equal(second, store.Get(_user, "svu"));
    }

    [Fact]
    public void Remove_ThenGet_Null()
    {
        var store = NewStore();
        store.Set(_user, Marker());
        store.Remove(_user, "svu");
        Assert.Null(store.Get(_user, "svu"));
        Assert.Null(NewStore().Get(_user, "svu"));
    }

    [Fact]
    public void Survives_NewInstance()
    {
        var marker = Marker();
        NewStore().Set(_user, marker);
        Assert.Equal(marker, NewStore().Get(_user, "svu"));
    }

    [Fact]
    public void PerUser_Isolated()
    {
        var store = NewStore();
        store.Set(_user, Marker());
        Assert.Null(store.Get(Guid.NewGuid(), "svu"));
    }

    [Fact]
    public void MissingFile_GetNull()
        => Assert.Null(NewStore().Get(_user, "svu"));

    [Fact]
    public void CorruptFile_TreatedAsEmpty()
    {
        File.WriteAllText(Path.Combine(_dataPath, "rewatch-markers.json"), "{nope");
        var store = NewStore();
        Assert.Null(store.Get(_user, "svu"));
        var marker = Marker();
        store.Set(_user, marker);
        Assert.Equal(marker, NewStore().Get(_user, "svu"));
    }

    [Fact]
    public void ConcurrentSets_FileStaysReadable()
    {
        var store = NewStore();
        Parallel.For(0, 50, i => store.Set(_user, Marker("series-" + i)));
        var reloaded = NewStore();
        for (var i = 0; i < 50; i++)
        {
            Assert.NotNull(reloaded.Get(_user, "series-" + i));
        }
    }
}
