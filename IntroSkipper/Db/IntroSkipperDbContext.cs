// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
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
    }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the segments.
    /// </summary>
    public DbSet<DbSegment> DbSegment { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the season information.
    /// </summary>
    public DbSet<DbSeasonInfo> DbSeasonInfo { get; set; }

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
            entity.HasKey(s => new { s.ItemId, s.Type });

            entity.HasIndex(e => e.ItemId);

            entity.Property(e => e.Start)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.End)
                  .HasDefaultValue(0.0)
                  .IsRequired();
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
        // If migrations table exists, just apply pending migrations normally
        if (Database.GetAppliedMigrations().Any() || !Database.CanConnect())
        {
            Database.Migrate();
            return;
        }

        // For databases without migration history
        try
        {
            // Backup existing data
            List<DbSegment> segments;
            List<DbSeasonInfo> seasonInfos;
            using (var db = new IntroSkipperDbContext(_dbPath))
            {
                segments = [.. db.DbSegment];
                seasonInfos = [.. db.DbSeasonInfo];
            }

            // Delete old database
            Database.EnsureDeleted();

            // Create new database with proper migration history
            Database.Migrate();

            // Restore the data
            using (var db = new IntroSkipperDbContext(_dbPath))
            {
                db.DbSegment.AddRange(segments);
                db.DbSeasonInfo.AddRange(seasonInfos);
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to apply migrations", ex);
        }
    }
}
