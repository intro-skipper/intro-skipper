// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
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
    private readonly string? _dbPath;

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
        _dbPath = null;
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
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder
                .UseSqlite($"Data Source={_dbPath}");
        }
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
    /// Uses synchronous EF Core APIs to avoid sync-over-async deadlock risks.
    /// </summary>
    public void ApplyMigrations()
    {
        Database.Migrate();
    }

    /// <summary>
    /// Asynchronously applies any pending migrations to the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously rebuilds the database while attempting to preserve valid segments and season information.
    /// </summary>
    /// <param name="contextFactory">Factory delegate to create sibling <see cref="IntroSkipperDbContext"/> instances.</param>
    /// <param name="forceCleanOnBackupFailure">
    /// When <c>true</c>, rebuild proceeds with an empty database if the backup read fails.
    /// When <c>false</c>, the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RebuildDatabaseAsync(Func<IntroSkipperDbContext> contextFactory, bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        var segments = new List<DbSegment>();
        var seasonInfos = new List<DbSeasonInfo>();
        var backupFailed = false;

        // Best-effort backup — a corrupted DB will fail here, and that's fine.
        try
        {
            using var db = contextFactory();
            segments = await db.DbSegment.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            segments = [.. segments.Where(s => s.ToSegment().Valid)];
            seasonInfos = await db.DbSeasonInfo.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or JsonException)
        {
            if (!forceCleanOnBackupFailure)
            {
                throw new InvalidOperationException("Failed to back up the existing database before rebuild. Aborting rebuild to avoid data loss.", ex);
            }

            // Explicit clean-rebuild fallback requested by the caller.
            backupFailed = true;
        }

        if (backupFailed)
        {
            DeleteDatabaseFiles();
        }
        else
        {
            await Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
        }

        await Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        // Restore whatever data was salvaged
        if (segments.Count > 0 || seasonInfos.Count > 0)
        {
            using var db = contextFactory();
            if (segments.Count > 0)
            {
                db.DbSegment.AddRange(segments);
            }

            if (seasonInfos.Count > 0)
            {
                db.DbSeasonInfo.AddRange(seasonInfos);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void DeleteDatabaseFiles()
    {
        var dbPath = GetDatabaseFilePath();
        if (string.IsNullOrEmpty(dbPath))
        {
            throw new InvalidOperationException("Cannot delete a database file when the context was created without a configured database path.");
        }

        // Close this context's own connection before clearing pools, so nothing holds a lock.
        Database.CloseConnection();
        SqliteConnection.ClearAllPools();

        // Attempt to delete all files, collecting failures so one locked file doesn't prevent the rest.
        List<(string Path, Exception Exception)>? failures = null;
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }.Where(File.Exists))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures ??= [];
                failures.Add((path, ex));
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                $"Failed to delete {failures.Count} database file(s): {string.Join(", ", failures.Select(f => f.Path))}",
                failures.Select(f => f.Exception));
        }
    }

    private string? GetDatabaseFilePath()
    {
        if (!string.IsNullOrEmpty(_dbPath))
        {
            return _dbPath;
        }

        var connectionString = Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource is not (null or "" or ":memory:") ? builder.DataSource : null;
    }
}
