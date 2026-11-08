using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.HomeRows.Configuration;
using Jellyfin.HomeRows.Providers;
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
}
