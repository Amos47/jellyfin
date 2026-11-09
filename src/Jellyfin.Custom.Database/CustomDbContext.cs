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

    /// <summary>
    /// Gets the discovery lists.
    /// </summary>
    public DbSet<CustomDiscoveryList> DiscoveryLists => Set<CustomDiscoveryList>();

    /// <summary>
    /// Gets the log of discovery films shown.
    /// </summary>
    public DbSet<CustomDiscoveryShown> DiscoveryShown => Set<CustomDiscoveryShown>();

    /// <summary>
    /// Gets the salts of re-picked days.
    /// </summary>
    public DbSet<CustomDiscoveryPickSalt> DiscoveryPickSalts => Set<CustomDiscoveryPickSalt>();

    /// <summary>
    /// Gets the saved discovery picks of each user and day.
    /// </summary>
    public DbSet<CustomDiscoveryPickSnapshot> DiscoveryPickSnapshots => Set<CustomDiscoveryPickSnapshot>();

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

        modelBuilder.Entity<CustomDiscoveryList>(entity =>
        {
            entity.ToTable("CustomDiscoveryLists");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).ValueGeneratedNever();
            entity.Property(e => e.Group).HasMaxLength(100);
        });

        modelBuilder.Entity<CustomDiscoveryShown>(entity =>
        {
            entity.ToTable("CustomDiscoveryShown");
            entity.HasKey(e => e.TmdbId);
            entity.Property(e => e.TmdbId).ValueGeneratedNever();
        });

        modelBuilder.Entity<CustomDiscoveryPickSalt>(entity =>
        {
            entity.ToTable("CustomDiscoveryPickSalts");
            entity.HasKey(e => e.Day);
            entity.Property(e => e.Day).ValueGeneratedNever();
        });

        modelBuilder.Entity<CustomDiscoveryPickSnapshot>(entity =>
        {
            entity.ToTable("CustomDiscoveryPickSnapshots");
            entity.HasKey(e => new { e.UserId, e.Day });
            entity.HasIndex(e => e.Day);
        });
    }
}
