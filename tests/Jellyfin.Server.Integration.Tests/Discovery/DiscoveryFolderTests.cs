using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Discovery;

public sealed class DiscoveryFolderTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public DiscoveryFolderTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Folder_IsRegisteredUnderRoot_AfterStartup()
    {
        _ = _factory.CreateClient();
        var locator = _factory.Services.GetRequiredService<IDiscoveryFolderLocator>();
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        var folder = locator.GetFolder();
        var root = libraryManager.RootFolder;

        Assert.True(folder.ParentId.Equals(root.Id));
        Assert.True(folder.Id.Equals(locator.FolderId));
        Assert.Contains(root.VirtualChildren, child => child.Id.Equals(locator.FolderId));
    }

    [Fact]
    public async Task Folder_SurvivesValidation()
    {
        _ = _factory.CreateClient();
        var locator = _factory.Services.GetRequiredService<IDiscoveryFolderLocator>();
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();

        await libraryManager.ValidateMediaLibrary(new Progress<double>(), TestContext.Current.CancellationToken);

        Assert.NotNull(libraryManager.GetItemById(locator.FolderId));
    }

    [Fact]
    public async Task Folder_NotInUserViews()
    {
        _ = _factory.CreateClient();
        var locator = _factory.Services.GetRequiredService<IDiscoveryFolderLocator>();
        var userManager = _factory.Services.GetRequiredService<IUserManager>();
        var userViewManager = _factory.Services.GetRequiredService<IUserViewManager>();
        var user = await userManager.CreateUserAsync("discovery-" + Guid.NewGuid().ToString("N")[..8]);

        var views = userViewManager.GetUserViews(new UserViewQuery { User = user });

        Assert.DoesNotContain(views, view => view.Id.Equals(locator.FolderId));
    }
}
