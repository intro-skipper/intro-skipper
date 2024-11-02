// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Plugin database.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
/// </remarks>
/// <param name="dbPath">The path to the SQLite database file.</param>
public class IntroSkipperDbContext(string dbPath) : DbContext
{
    private readonly string _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the segments.
    /// </summary>
    public DbSet<DbSegment> DbSegment { get; set; } = null!;

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the season information.
    /// </summary>
    public DbSet<DbSeasonInfo> DbSeasonInfo { get; set; } = null!;

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}")
                     .EnableSensitiveDataLogging(false);
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbSegment>(entity =>
        {
            entity.ToTable("DbSegment");
            entity.HasKey(s => new { s.ItemId, s.Type });

            entity.Property(e => e.ItemId)
                  .IsRequired();

            entity.Property(e => e.Type)
                  .IsRequired();

            entity.Property(e => e.Start);

            entity.Property(e => e.End);
        });

        modelBuilder.Entity<DbSeasonInfo>(entity =>
        {
            entity.ToTable("DbSeasonInfo");
            entity.HasKey(s => new { s.SeasonId, s.Type });

            entity.Property(e => e.SeasonId)
                  .IsRequired();

            entity.Property(e => e.Type)
                  .IsRequired();

            entity.Property(e => e.Action);
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Applies any pending migrations to the database.
    /// </summary>
    public void ApplyMigrations()
    {
        Database.Migrate();
    }
}
