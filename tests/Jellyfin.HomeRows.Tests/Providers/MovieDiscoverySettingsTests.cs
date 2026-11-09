using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Providers;
using MediaBrowser.Controller.Discovery;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class MovieDiscoverySettingsTests
{
    private readonly Mock<IHomeRowConfigurationService> _configuration = new();
    private readonly User _user = new("tester", "Default", "Default");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MovieDiscoverySettings Create(params EffectiveHomeRow[] rows)
    {
        _configuration
            .Setup(c => c.GetEffectiveLayoutAsync(_user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveHomeRowLayout(HomeRowLayoutSource.User, rows));
        return new MovieDiscoverySettings(_configuration.Object);
    }

    private static EffectiveHomeRow Row(string provider, JsonObject config, bool enabled = true, bool available = true)
        => new(new HomeRowInstance(Guid.NewGuid(), provider, null, config, enabled), available);

    private static EffectiveHomeRow Discovery(bool? externalTrailers, bool enabled = true)
        => Row(
            HomeRowProviderIds.MovieDiscovery,
            externalTrailers is { } value ? new JsonObject { ["externalTrailers"] = value } : new JsonObject(),
            enabled);

    [Fact]
    public async Task ExternalTrailers_NoDiscoveryRow_False()
    {
        Assert.False(await Create(Row(HomeRowProviderIds.NextUp, new JsonObject { ["externalTrailers"] = true })).GetExternalTrailersAsync(_user, Ct));
    }

    [Fact]
    public async Task ExternalTrailers_MissingSetting_False()
    {
        Assert.False(await Create(Discovery(null)).GetExternalTrailersAsync(_user, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExternalTrailers_ReadsTheRowSetting(bool value)
    {
        Assert.Equal(value, await Create(Discovery(value)).GetExternalTrailersAsync(_user, Ct));
    }

    [Fact]
    public async Task ExternalTrailers_AnyDiscoveryRowTrue_True()
    {
        Assert.True(await Create(Discovery(false), Discovery(true), Discovery(null)).GetExternalTrailersAsync(_user, Ct));
    }

    [Fact]
    public async Task ExternalTrailers_DisabledRowIgnored()
    {
        Assert.False(await Create(Discovery(true, enabled: false), Discovery(false)).GetExternalTrailersAsync(_user, Ct));
    }

    [Fact]
    public async Task ExternalTrailers_NonBoolValue_False()
    {
        Assert.False(await Create(Row(HomeRowProviderIds.MovieDiscovery, new JsonObject { ["externalTrailers"] = "yes" })).GetExternalTrailersAsync(_user, Ct));
    }

    private MovieDiscoverySettings CreateStored(IReadOnlyList<HomeRowInstance>? adminDefault, params IReadOnlyList<HomeRowInstance>[] userLayouts)
    {
        _configuration.Setup(c => c.GetAdminDefaultAsync(It.IsAny<CancellationToken>())).ReturnsAsync(adminDefault);
        _configuration.Setup(c => c.GetUserLayoutsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(userLayouts);
        return new MovieDiscoverySettings(_configuration.Object);
    }

    private static HomeRowInstance Stored(string provider, int? minReadyFilms, bool enabled = true)
        => new(
            Guid.NewGuid(),
            provider,
            null,
            minReadyFilms is { } value ? new JsonObject { ["minReadyFilms"] = value } : new JsonObject(),
            enabled);

    [Fact]
    public async Task ReadyThreshold_NoDiscoveryRow_Default()
    {
        Assert.Equal(DiscoveryPickRules.DefaultMinReadyFilms, await CreateStored(null).GetReadyThresholdAsync(Ct));
        Assert.Equal(
            DiscoveryPickRules.DefaultMinReadyFilms,
            await CreateStored([Stored(HomeRowProviderIds.NextUp, 12)], [Stored(HomeRowProviderIds.MovieDiscovery, 15, enabled: false)]).GetReadyThresholdAsync(Ct));
    }

    [Fact]
    public async Task ReadyThreshold_LargestAcrossAdminDefaultAndUserLayouts()
    {
        Assert.Equal(3, await CreateStored([Stored(HomeRowProviderIds.MovieDiscovery, 3)]).GetReadyThresholdAsync(Ct));
        Assert.Equal(
            8,
            await CreateStored(
                [Stored(HomeRowProviderIds.MovieDiscovery, 3)],
                [Stored(HomeRowProviderIds.MovieDiscovery, 8), Stored(HomeRowProviderIds.MovieDiscovery, 15, enabled: false)],
                [Stored(HomeRowProviderIds.MovieDiscovery, 2)]).GetReadyThresholdAsync(Ct));

        // A row without the setting counts as the default; an out-of-range value is clamped as the row reads it.
        Assert.Equal(5, await CreateStored(null, [Stored(HomeRowProviderIds.MovieDiscovery, 1), Stored(HomeRowProviderIds.MovieDiscovery, null)]).GetReadyThresholdAsync(Ct));
        Assert.Equal(20, await CreateStored(null, [Stored(HomeRowProviderIds.MovieDiscovery, 99)]).GetReadyThresholdAsync(Ct));
    }
}
