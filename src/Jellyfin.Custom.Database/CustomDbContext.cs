using Jellyfin.Custom.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Custom.Database;

/// <summary>
/// The context for the fork's own tables. It has its own migrations and history table so that
/// it never takes part in upstream's migration chain.
/// </summary>
/// <param name="options">The db context options.</param>
public class CustomDbContext(DbContextOptions<CustomDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The name of the migrations history table of this context.
    /// </summary>
    public const string MigrationsHistoryTableName = "__CustomMigrationsHistory";

    /// <summary>
    /// Gets the per user home row layouts.
    /// </summary>
    public DbSet<CustomHomeRowConfiguration> HomeRowConfigurations => Set<CustomHomeRowConfiguration>();

    /// <summary>
    /// Gets the server wide default home row layout.
    /// </summary>
    public DbSet<CustomHomeRowDefault> HomeRowDefaults => Set<CustomHomeRowDefault>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CustomHomeRowConfiguration>(entity =>
        {
            entity.ToTable("CustomHomeRowConfigurations");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).ValueGeneratedNever();
        });

        modelBuilder.Entity<CustomHomeRowDefault>(entity =>
        {
            entity.ToTable("CustomHomeRowDefaults");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });
    }
}
