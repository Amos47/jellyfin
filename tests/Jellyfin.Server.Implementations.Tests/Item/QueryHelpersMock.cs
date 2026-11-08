using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Persistence;
using Moq;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Fork: a mocked <see cref="IItemQueryHelpers"/> whose discovery guard passes items through, so repository tests
/// without a discovery folder query like upstream (a bare mock answers an empty in-memory queryable EF can't translate).
/// </summary>
internal static class QueryHelpersMock
{
    public static IItemQueryHelpers Create()
    {
        var helpers = new Mock<IItemQueryHelpers>();
        helpers.Setup(h => h.ApplyDiscoveryGuard(It.IsAny<JellyfinDbContext>(), It.IsAny<IQueryable<BaseItemEntity>>()))
            .Returns((JellyfinDbContext _, IQueryable<BaseItemEntity> items) => items);
        return helpers.Object;
    }
}
