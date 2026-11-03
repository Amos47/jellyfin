using System;
using System.Linq;
using Moq;
using Xunit;

namespace Jellyfin.HomeRows.Tests;

public class HomeRowProviderFactoryTests
{
    private static IHomeRowProvider CreateProvider(string id)
    {
        var provider = new Mock<IHomeRowProvider>();
        provider.SetupGet(p => p.Id).Returns(id);
        return provider.Object;
    }

    [Fact]
    public void Get_ReturnsProviderById()
    {
        var a = CreateProvider("a");
        var b = CreateProvider("b");
        var factory = new HomeRowProviderFactory([a, b]);

        Assert.Same(b, factory.Get("b"));
        Assert.Same(a, factory.Get("a"));
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var factory = new HomeRowProviderFactory([CreateProvider("a")]);

        Assert.Null(factory.Get("missing"));
        Assert.Null(factory.Get("A"));
    }

    [Fact]
    public void Ctor_DuplicateIds_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new HomeRowProviderFactory([CreateProvider("a"), CreateProvider("a")]));
    }

    [Fact]
    public void Providers_KeepRegistrationOrder()
    {
        var providers = new[] { CreateProvider("c"), CreateProvider("a"), CreateProvider("b") };
        var factory = new HomeRowProviderFactory(providers);

        Assert.Equal(providers, factory.Providers.ToArray());
    }

    [Fact]
    public void Ctor_NoProviders_Works()
    {
        var factory = new HomeRowProviderFactory([]);

        Assert.Empty(factory.Providers);
        Assert.Null(factory.Get("a"));
    }
}
