using Jellyfin.Custom.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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

    /// <summary>
    /// Gets the users' named lists of films.
    /// </summary>
    public DbSet<CustomUserList> UserLists => Set<CustomUserList>();

    /// <summary>
    /// Gets the films of the users' lists.
    /// </summary>
    public DbSet<CustomUserListItem> UserListItems => Set<CustomUserListItem>();

    /// <summary>
    /// Gets the markers of the one-time data changes that ran.
    /// </summary>
    public DbSet<CustomMarker> Markers => Set<CustomMarker>();

    /// <summary>
    /// Gets the download requests of discovery films.
    /// </summary>
    public DbSet<CustomDiscoveryRequest> DiscoveryRequests => Set<CustomDiscoveryRequest>();

    /// <summary>
    /// Gets the users who asked for each download.
    /// </summary>
    public DbSet<CustomDiscoveryRequestUser> DiscoveryRequestUsers => Set<CustomDiscoveryRequestUser>();

    /// <summary>
    /// Gets the server-wide discovery settings.
    /// </summary>
    public DbSet<CustomDiscoverySetting> DiscoverySettings => Set<CustomDiscoverySetting>();

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
            MediaTypeColumn(entity.Property(e => e.ItemType));
        });

        modelBuilder.Entity<CustomDiscoveryShown>(entity =>
        {
            entity.ToTable("CustomDiscoveryShown");
            entity.HasKey(e => new { e.TmdbId, e.MediaType });
            entity.Property(e => e.TmdbId).ValueGeneratedNever();
            MediaTypeColumn(entity.Property(e => e.MediaType));
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
            entity.HasKey(e => new { e.UserId, e.Day, e.MediaType });
            entity.HasIndex(e => e.Day);
            MediaTypeColumn(entity.Property(e => e.MediaType));
        });

        modelBuilder.Entity<CustomUserList>(entity =>
        {
            entity.ToTable("CustomUserLists");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.HasIndex(e => e.UserId);

            // One default list per user, even if two first reads create it at once.
            entity.HasIndex(e => e.UserId, "IX_CustomUserLists_UserId_Default").IsUnique().HasFilter("\"IsDefault\" = 1");
            entity.HasMany(e => e.Items).WithOne().HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomUserListItem>(entity =>
        {
            entity.ToTable("CustomUserListItems");
            entity.HasKey(e => new { e.ListId, e.TmdbId, e.MediaType });
            entity.Property(e => e.ImdbId).HasMaxLength(32);
            MediaTypeColumn(entity.Property(e => e.MediaType));
            entity.HasIndex(e => e.TmdbId);
        });

        modelBuilder.Entity<CustomMarker>(entity =>
        {
            entity.ToTable("CustomMarkers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(100).ValueGeneratedNever();
        });

        modelBuilder.Entity<CustomDiscoveryRequest>(entity =>
        {
            entity.ToTable("CustomDiscoveryRequests");
            entity.HasKey(e => new { e.TmdbId, e.MediaType });
            entity.Property(e => e.TmdbId).ValueGeneratedNever();
            MediaTypeColumn(entity.Property(e => e.MediaType));
            entity.Property(e => e.State).HasMaxLength(32);
            entity.HasIndex(e => e.State);
            entity.HasMany(e => e.Users).WithOne().HasForeignKey(u => new { u.TmdbId, u.MediaType }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomDiscoveryRequestUser>(entity =>
        {
            entity.ToTable("CustomDiscoveryRequestUsers");
            entity.HasKey(e => new { e.TmdbId, e.MediaType, e.UserId });
            MediaTypeColumn(entity.Property(e => e.MediaType));
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<CustomDiscoverySetting>(entity =>
        {
            entity.ToTable("CustomDiscoverySettings");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(100).ValueGeneratedNever();
        });
    }

    // A media type column: a short name, Movie for the rows from before shows. Never generated: it is part of keys, and a
    // key EF thinks the database fills in makes a new row with it set look like an existing one, so it isn't inserted.
    private static void MediaTypeColumn(PropertyBuilder<string> property)
        => property.HasMaxLength(CustomMediaTypes.MaxLength).HasDefaultValue(CustomMediaTypes.Movie).ValueGeneratedNever();
}
