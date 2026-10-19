using Jellyfin.Custom.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.EfMigrations;

public class CustomEfMigrationTests
{
    [Fact]
    public void Migrations_HaveNoPendingModelChanges()
    {
        using var context = new CustomDesignTimeDbFactory().CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges(), "There are unapplied changes to the custom EFCore model. Please create a Migration.");
    }
}
