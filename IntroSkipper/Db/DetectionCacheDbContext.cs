// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// EF Core database context for the FFmpeg detection cache.
/// Stored in a separate SQLite file (<c>introskipper-cache.db</c>) so cache corruption
/// does not affect the main segment/season database.
/// </summary>
public class DetectionCacheDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options, configured through <see cref="SqlitePragmas.Configure"/>.</param>
    public DetectionCacheDbContext(DbContextOptions<DetectionCacheDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the detection cache entries.
    /// </summary>
    public DbSet<DbDetectionCache> DetectionCache => Set<DbDetectionCache>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbDetectionCache>(entity =>
        {
            entity.ToTable("DetectionCache");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                  .ValueGeneratedOnAdd();

            // Composite unique index: one cache entry per (ItemId, Mode, Type, Start, End).
            entity.HasIndex(e => new { e.ItemId, e.Mode, e.Type, e.Start, e.End })
                  .HasDatabaseName("IX_DetectionCache_Unique")
                  .IsUnique();

            entity.HasIndex(e => e.Mode);

            entity.Property(e => e.Start)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.End)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.Data)
                  .IsRequired();

            entity.Property(e => e.ConfigHash)
                  .HasDefaultValue(string.Empty)
                  .IsRequired();
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Ensures the database and schema are created.
    /// Uses <c>EnsureCreated</c> rather than migrations because this is a cache database
    /// with no schema evolution requirements — it can safely be deleted and recreated.
    /// </summary>
    public void EnsureSchema()
    {
        try
        {
            Database.EnsureCreated();
            DetectionCache.AsNoTracking().Take(1).Load();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            Database.EnsureDeleted();
            Database.EnsureCreated();
        }
    }
}
