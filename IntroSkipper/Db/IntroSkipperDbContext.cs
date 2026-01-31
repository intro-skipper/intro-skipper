// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Linq;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Plugin database.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
/// </remarks>
public class IntroSkipperDbContext : DbContext
{
    private readonly string _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
    /// </summary>
    /// <param name="dbPath">The path to the SQLite database file.</param>
    public IntroSkipperDbContext(string dbPath)
    {
        _dbPath = dbPath;
        DbSegment = Set<DbSegment>();
        DbSeasonInfo = Set<DbSeasonInfo>();
        DbSegmentOutbox = Set<DbSegmentOutbox>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    public IntroSkipperDbContext(DbContextOptions<IntroSkipperDbContext> options) : base(options)
    {
        var folder = Environment.SpecialFolder.LocalApplicationData;
        var path = Environment.GetFolderPath(folder);
        _dbPath = System.IO.Path.Join(path, "introskipper.db");
        DbSegment = Set<DbSegment>();
        DbSeasonInfo = Set<DbSeasonInfo>();
        DbSegmentOutbox = Set<DbSegmentOutbox>();
    }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the segments.
    /// </summary>
    public DbSet<DbSegment> DbSegment { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the season information.
    /// </summary>
    public DbSet<DbSeasonInfo> DbSeasonInfo { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the outbox entries.
    /// </summary>
    public DbSet<DbSegmentOutbox> DbSegmentOutbox { get; set; }

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbSegment>(entity =>
        {
            entity.ToTable("DbSegment");
            entity.HasKey(s => s.Id);

            entity.HasIndex(e => new { e.ItemId, e.Type });

            entity.HasIndex(e => new { e.SeasonId, e.Type });

            entity.Property(e => e.Start)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.End)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.IsFirstAppearance)
                  .HasDefaultValue(false)
                  .IsRequired();
        });

        modelBuilder.Entity<DbSegmentOutbox>(entity =>
        {
            entity.ToTable("DbSegmentOutbox");
            entity.HasKey(e => e.Id);

            // Index for cleanup queries
            entity.HasIndex(e => e.ProcessedAt);

            // Index for grouping by item
            entity.HasIndex(e => new { e.ItemId, e.ProcessedAt });

            // Covering index for pending query: WHERE ProcessedAt IS NULL AND ClaimedBy IS NULL AND RetryCount < N ORDER BY CreatedAt
            entity.HasIndex(e => new { e.ProcessedAt, e.ClaimedBy, e.RetryCount, e.CreatedAt })
                  .HasDatabaseName("IX_DbSegmentOutbox_Pending");

            entity.Property(e => e.CreatedAt)
                  .HasDefaultValueSql("datetime('now')")
                  .IsRequired();

            entity.Property(e => e.RetryCount)
                  .HasDefaultValue(0)
                  .IsRequired();

            entity.Property(e => e.ClaimedBy)
                  .HasMaxLength(64);
        });

        modelBuilder.Entity<DbSeasonInfo>(entity =>
        {
            entity.ToTable("DbSeasonInfo");
            entity.HasKey(s => new { s.SeasonId, s.Type });

            entity.HasIndex(e => e.SeasonId);

            entity.Property(e => e.Action)
                  .HasDefaultValue(AnalyzerAction.Default)
                  .IsRequired();
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Applies any pending migrations to the database.
    /// </summary>
    public void ApplyMigrations()
    {
        // If database doesn't exist, Migrate() will create it
        if (!Database.CanConnect())
        {
            Database.Migrate();
            return;
        }

        // If migrations table exists and has history, apply pending migrations
        if (Database.GetAppliedMigrations().Any())
        {
            Database.Migrate();
            return;
        }

        // Legacy database without migration history - delete and recreate
        // Data will be repopulated on re-analysis
        Database.EnsureDeleted();
        Database.Migrate();
    }
}
