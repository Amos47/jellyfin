using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jellyfin.Custom.Database;

/// <summary>
/// The design time factory for <see cref="CustomDbContext"/>.
/// This is only used for the creation of migrations and not during runtime.
/// </summary>
public sealed class CustomDesignTimeDbFactory : IDesignTimeDbContextFactory<CustomDbContext>
{
    /// <inheritdoc />
    public CustomDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CustomDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=custom-jelly.db",
            o => o.MigrationsAssembly(typeof(CustomDbContext).Assembly)
                .MigrationsHistoryTable(CustomDbContext.MigrationsHistoryTableName));

        return new CustomDbContext(optionsBuilder.Options);
    }
}
