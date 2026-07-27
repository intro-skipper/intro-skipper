// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntroSkipper.Db;

/// <summary>
/// Plugin segment database (<c>introskipper-v2.db</c>). The schema is owned by a
/// single EF baseline migration; data from the legacy <c>introskipper.db</c> is
/// carried over once by <see cref="LegacyDatabaseImporter"/>.
/// </summary>
public class IntroSkipperDbContext : DbContext
{
    private static readonly SqlitePragmaInterceptor _pragmaInterceptor = new();

    private readonly string? _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
    /// </summary>
    /// <param name="dbPath">The path to the SQLite database file.</param>
    public IntroSkipperDbContext(string dbPath)
    {
        _dbPath = dbPath;
        Segments = Set<DbSegment>();
        SeasonStates = Set<DbSeasonState>();
        ImportHistory = Set<DbImportRecord>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <remarks>
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> disambiguates for the DI
    /// factory registered by <c>AddDbContextFactory</c>: with two public constructors,
    /// EF's factory source falls back to <c>ActivatorUtilities</c>, which requires a
    /// single unambiguous constructor.
    /// </remarks>
    [ActivatorUtilitiesConstructor]
    public IntroSkipperDbContext(DbContextOptions<IntroSkipperDbContext> options) : base(options)
    {
        _dbPath = null;
        Segments = Set<DbSegment>();
        SeasonStates = Set<DbSeasonState>();
        ImportHistory = Set<DbImportRecord>();
    }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the segments.
    /// </summary>
    public DbSet<DbSegment> Segments { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the season state.
    /// </summary>
    public DbSet<DbSeasonState> SeasonStates { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the legacy-import markers.
    /// </summary>
    public DbSet<DbImportRecord> ImportHistory { get; set; }

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder
                .UseSqlite($"Data Source={_dbPath}")
                .AddInterceptors(_pragmaInterceptor);
        }
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbSegment>(entity =>
        {
            entity.ToTable("Segments");
            entity.HasKey(s => s.Id);

            // Ids are always supplied by the plugin (Guid v7) so they can be shared
            // with Jellyfin's MediaSegments rows.
            entity.Property(e => e.Id)
                  .ValueGeneratedNever();

            // One uniform uniqueness rule for every mode: exact duplicates of the same
            // range are rejected, any number of distinct segments per (item, type) is fine.
            // The ItemId prefix also serves the per-item lookup, so no extra index.
            entity.HasIndex(e => new { e.ItemId, e.Type, e.StartTicks, e.EndTicks })
                  .HasDatabaseName("IX_Segments_ItemId_Type_StartTicks_EndTicks")
                  .IsUnique();

            entity.Property(e => e.ConfigHash)
                  .IsRequired();

            entity.Property(e => e.CreatedAt)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            entity.Property(e => e.UpdatedAt)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });

        modelBuilder.Entity<DbSeasonState>(entity =>
        {
            entity.ToTable("SeasonStates");
            entity.HasKey(s => new { s.SeasonId, s.Type });

            entity.Property(e => e.ConfigHash)
                  .IsRequired();
        });

        modelBuilder.Entity<DbImportRecord>(entity =>
        {
            entity.ToTable("ImportHistory");
            entity.HasKey(r => r.Id);

            entity.Property(r => r.ImportedAt)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            entity.Property(r => r.Notes)
                  .IsRequired();
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

    /// <inheritdoc/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampSegmentTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc/>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampSegmentTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Asynchronously rebuilds the database while attempting to preserve valid segments,
    /// season state and the legacy-import marker.
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
        var seasonStates = new List<DbSeasonState>();
        var importRecords = new List<DbImportRecord>();
        var backupFailed = false;

        // Best-effort backup — a corrupted DB will fail here, and that's fine.
        try
        {
            using var db = contextFactory();
            var connection = db.Database.GetDbConnection();
            var wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen)
            {
                await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                segments = await db.Segments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

                // Suppressed rows are salvaged too: tombstones are user intent.
                segments = [.. segments.Where(s => s.StartTicks >= 0 && s.EndTicks > s.StartTicks)];
                seasonStates = await db.SeasonStates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                importRecords = await db.ImportHistory.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!wasOpen)
                {
                    await db.Database.CloseConnectionAsync().ConfigureAwait(false);
                }
            }
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

        // A rebuilt database must never re-run the legacy import on top of the restored
        // rows, so synthesize a marker when none could be salvaged.
        if (importRecords.Count == 0)
        {
            importRecords.Add(new DbImportRecord
            {
                ImportedAt = DateTime.UtcNow,
                SourceFileFound = false,
                Notes = "rebuild"
            });
        }

        // Auto-increment keys must not be restored verbatim into the fresh table.
        foreach (var record in importRecords)
        {
            record.Id = 0;
        }

        using (var db = contextFactory())
        {
            if (segments.Count > 0)
            {
                db.Segments.AddRange(segments);
            }

            if (seasonStates.Count > 0)
            {
                db.SeasonStates.AddRange(seasonStates);
            }

            db.ImportHistory.AddRange(importRecords);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stamps <see cref="DbSegment.CreatedAt"/>/<see cref="DbSegment.UpdatedAt"/> on tracked
    /// writes. Inserted rows only receive values when unset so restored snapshots keep their
    /// original timestamps. <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> bypass the change
    /// tracker and therefore this stamping; segment writes must stay tracked.
    /// </summary>
    private void StampSegmentTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<DbSegment>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }

                if (entry.Entity.UpdatedAt == default)
                {
                    entry.Entity.UpdatedAt = now;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
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

    internal string? GetDatabaseFilePath()
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
