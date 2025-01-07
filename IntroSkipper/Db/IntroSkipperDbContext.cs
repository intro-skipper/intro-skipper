// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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

            entity.Property(e => e.EpisodeIds)
                  .HasConversion(
                      v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                      v => JsonSerializer.Deserialize<IEnumerable<Guid>>(v, (JsonSerializerOptions?)null) ?? new List<Guid>(),
                      new ValueComparer<IEnumerable<Guid>>(
                          (c1, c2) => (c1 ?? new List<Guid>()).SequenceEqual(c2 ?? new List<Guid>()),
                          c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                          c => c.ToList()));
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Applies any pending migrations to the database.
    /// </summary>
    public void ApplyMigrations()
    {
        // If database doesn't exist or can't connect, create it with migrations
        if (!Database.CanConnect())
        {
            Database.Migrate();
            return;
        }

        // If migrations table exists, apply pending migrations normally
        if (Database.GetAppliedMigrations().Any())
        {
            Database.Migrate();
            return;
        }

        // For databases without migration history
        RebuildDatabase();
    }

    /// <summary>
    /// Rebuilds the database while preserving valid segments and season information.
    /// </summary>
    public void RebuildDatabase()
    {
        // Check if database file exists
        if (!File.Exists(_dbPath))
        {
            Database.EnsureDeleted();
            Database.EnsureCreated();
            Database.Migrate();
            return;
        }

        List<DbSegment> segments = [];
        List<DbSeasonInfo> seasonInfos = [];

        // Try to backup existing data if possible
        using (var db = new IntroSkipperDbContext(_dbPath))
        {
            try
            {
                // Check if tables exist
                if (Database.GetAppliedMigrations().Any())
                {
                    segments = [.. db.DbSegment.AsEnumerable().Where(s => s.ToSegment().Valid)];
                    seasonInfos = [.. db.DbSeasonInfo];
                }
            }
            catch (Exception ex)
            {
                // Log and ignore errors when reading old data
                System.Diagnostics.Debug.WriteLine($"Failed to read existing data: {ex.Message}");
            }
        }

        try
        {
            // Delete and recreate database
            Database.EnsureDeleted();
            Database.Migrate();

            // Restore data if available
            using var newDb = new IntroSkipperDbContext(_dbPath);
            if (segments.Count > 0)
            {
                newDb.DbSegment.AddRange(segments);
            }

            if (seasonInfos.Count > 0)
            {
                newDb.DbSeasonInfo.AddRange(seasonInfos);
            }

            newDb.SaveChanges();
        }
        catch (Exception ex)
        {
            // Last resort: Create new empty database
            Database.EnsureDeleted();
            Database.Migrate();
            throw new InvalidOperationException("Failed to rebuild database, created empty one", ex);
        }
    }
}
