// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IntroSkipper.Db;

/// <summary>
/// Plugin segment database (<c>introskipper-v2.db</c>). The schema is owned by EF
/// migrations (a single baseline; later changes are plain migrations on top); data from
/// the legacy <c>introskipper.db</c> is carried over once by <see cref="LegacyDatabaseImporter"/>.
/// </summary>
public class IntroSkipperDbContext : DbContext
{
    /// <summary>
    /// Rows per <see cref="SaveBatchAsync"/> flush; shared by the salvage restore and the
    /// legacy import so the bounded-batch pattern cannot drift between the two bulk paths.
    /// </summary>
    internal const int SaveBatchSize = 1000;

    // SQLite stores DateTime without a kind; every stored timestamp is UTC, so reads
    // must come back marked as such or comparisons silently use local time.
    private static readonly ValueConverter<DateTime, DateTime> _utcDateTimeConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> _utcNullableDateTimeConverter =
        new(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
    /// </summary>
    /// <param name="options">The options, configured through <see cref="SqlitePragmas.Configure"/> (the rebuild's scratch context uses a private in-memory connection).</param>
    public IntroSkipperDbContext(DbContextOptions<IntroSkipperDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the segments.
    /// </summary>
    public DbSet<DbSegment> Segments => Set<DbSegment>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the season state.
    /// </summary>
    public DbSet<DbSeasonState> SeasonStates => Set<DbSeasonState>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing per-season analysis-window overrides.
    /// </summary>
    public DbSet<DbSeasonAnalysisOverride> SeasonAnalysisOverrides => Set<DbSeasonAnalysisOverride>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the per-item analysis records.
    /// </summary>
    public DbSet<DbAnalyzedItem> AnalyzedItems => Set<DbAnalyzedItem>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the legacy-import markers.
    /// </summary>
    public DbSet<DbImportRecord> ImportHistory => Set<DbImportRecord>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the items whose automatic
    /// segments are withheld from Jellyfin.
    /// </summary>
    public DbSet<DbDisabledItem> DisabledItems => Set<DbDisabledItem>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the pending projection
    /// markers (one per item with work outstanding).
    /// </summary>
    public DbSet<DbProjectionQueueItem> ProjectionQueue => Set<DbProjectionQueueItem>();

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the durable foreign-row
    /// deletes awaiting projection.
    /// </summary>
    public DbSet<DbProjectionExternalOperation> ProjectionExternalOperations => Set<DbProjectionExternalOperation>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbSegment>(entity =>
        {
            // The range invariant is the database's, not only the facade's: no write path
            // (raw SQL, a future bug) can store a row that would fail every later mirror
            // sync of its item at the Jellyfin write boundary.
            entity.ToTable("Segments", table => table.HasCheckConstraint(
                "CK_Segments_Range",
                "\"EndTicks\" > \"StartTicks\" AND \"StartTicks\" >= 0"));
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
                  .HasConversion(_utcDateTimeConverter);

            entity.Property(e => e.UpdatedAt)
                  .HasConversion(_utcDateTimeConverter);
        });

        modelBuilder.Entity<DbSeasonState>(entity =>
        {
            entity.ToTable("SeasonStates");
            entity.HasKey(s => new { s.SeasonId, s.Type });
        });

        modelBuilder.Entity<DbSeasonAnalysisOverride>(entity =>
        {
            entity.ToTable("SeasonAnalysisOverrides");
            entity.HasKey(s => s.SeasonId);
        });

        modelBuilder.Entity<DbAnalyzedItem>(entity =>
        {
            entity.ToTable("AnalyzedItems");

            // One record per item and mode; the ItemId prefix serves the per-season
            // snapshot (ItemId IN ...) and the per-item clears.
            entity.HasKey(e => new { e.ItemId, e.Type });

            entity.Property(e => e.ConfigHash)
                  .IsRequired();
        });

        modelBuilder.Entity<DbImportRecord>(entity =>
        {
            entity.ToTable("ImportHistory");
            entity.HasKey(r => r.Id);

            entity.Property(r => r.ImportedAt)
                  .HasConversion(_utcDateTimeConverter);

            entity.Property(r => r.Notes)
                  .IsRequired();
        });

        modelBuilder.Entity<DbDisabledItem>(entity =>
        {
            entity.ToTable("DisabledItems");

            // One flag per item by construction; every read is by item ID.
            entity.HasKey(e => e.ItemId);

            // The key is always a real library item id — never client-generated. Without
            // this, EF's Guid-PK convention would silently substitute a random id for a
            // default ItemId instead of surfacing the caller's bug.
            entity.Property(e => e.ItemId)
                  .ValueGeneratedNever();
        });

        modelBuilder.Entity<DbProjectionQueueItem>(entity =>
        {
            entity.ToTable("ProjectionQueue");

            // One marker per item; the key is always a real library item id.
            entity.HasKey(e => e.ItemId);
            entity.Property(e => e.ItemId)
                  .ValueGeneratedNever();

            // Serves the due-work scan (NextAttemptAt IS NULL OR <= now).
            entity.HasIndex(e => e.NextAttemptAt);

            entity.Property(e => e.NextAttemptAt)
                  .HasConversion(_utcNullableDateTimeConverter);
        });

        modelBuilder.Entity<DbProjectionExternalOperation>(entity =>
        {
            entity.ToTable("ProjectionExternalOperations");
            entity.HasKey(e => e.Id);

            // Serves the per-item FIFO read during projection.
            entity.HasIndex(e => e.ItemId);
        });

        base.OnModelCreating(modelBuilder);
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
    /// Asynchronously rebuilds the database while attempting to preserve segments,
    /// season state, analysis records, disabled items and the legacy-import marker. When no marker exists
    /// (the legacy import never succeeded) none is written, so the next start retries the
    /// import; the importer skips rows the restored data already holds.
    /// </summary>
    /// <remarks>
    /// The replacement is migrated and restored in a private in-memory database, then copied
    /// over the live file in one SQLite transaction. A failed rebuild leaves the file as it
    /// was, and writes that commit while the rebuild runs are overwritten. Only the consented
    /// <paramref name="forceCleanOnBackupFailure"/> path deletes and recreates the file.
    /// </remarks>
    /// <param name="forceCleanOnBackupFailure">
    /// When <c>true</c> and the backup read fails, the file is recreated with only the tables read before the failure.
    /// When <c>false</c>, the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <exception cref="DatabaseRebuildBackupException">The backup read failed and <paramref name="forceCleanOnBackupFailure"/> is <c>false</c>; the database file is untouched.</exception>
    internal async Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        var segments = new List<DbSegment>();
        var seasonStates = new List<DbSeasonState>();
        var seasonAnalysisOverrides = new List<DbSeasonAnalysisOverride>();
        var analyzedItems = new List<DbAnalyzedItem>();
        var importRecords = new List<DbImportRecord>();
        var disabledItems = new List<DbDisabledItem>();
        var projectionQueue = new List<DbProjectionQueueItem>();
        var projectionExternalOperations = new List<DbProjectionExternalOperation>();

        // Best-effort backup — a corrupted DB will fail here, and that's fine.
        try
        {
            // Stays open for the copy at the end; disposing the context closes it.
            await Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Suppressed rows are salvaged too: tombstones are user intent. So is
            // pending projection work: markers and journaled foreign-row deletes
            // record durable obligations toward Jellyfin.
            segments = await Segments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            seasonStates = await SeasonStates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            seasonAnalysisOverrides = await SeasonAnalysisOverrides.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            analyzedItems = await AnalyzedItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            importRecords = await ImportHistory.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            disabledItems = await DisabledItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            projectionQueue = await ProjectionQueue.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            projectionExternalOperations = await ProjectionExternalOperations.AsNoTracking().OrderBy(o => o.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or JsonException or FormatException or InvalidCastException)
        {
            // FormatException/InvalidCastException cover corrupted TEXT in the
            // materialized Guid/DateTime columns, which surfaces during row
            // materialization rather than as a SqliteException.
            if (!forceCleanOnBackupFailure)
            {
                throw new DatabaseRebuildBackupException("Failed to back up the existing database before rebuild. Aborting rebuild to avoid data loss.", ex);
            }

            // Explicit consent to discard an unreadable file: recreate it, then restore
            // the tables read before the failure into it like any other rebuild.
            DeleteDatabaseFiles();
            await Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        // Rebuild targets corrupted databases, whose readable rows can still violate the
        // fresh schema's CHECK constraint, unique index or primary keys, and one such row
        // would fail the restore. Duplicates collapse with explicit precedence (user rows,
        // then tombstones, then automatic rows), so recovery never trades a user's segment
        // or deletion for an automatic row.
        segments = [.. segments
            .Where(s => s.StartTicks >= 0 && s.EndTicks > s.StartTicks)
            .OrderByDescending(s => s.Source == SegmentSource.User)
            .ThenByDescending(s => s.State == SegmentState.Suppressed)
            .DistinctBy(s => s.Id)
            .DistinctBy(s => (s.ItemId, s.Type, s.StartTicks, s.EndTicks))];
        seasonStates = [.. seasonStates.DistinctBy(s => (s.SeasonId, s.Type))];
        seasonAnalysisOverrides = [.. seasonAnalysisOverrides.DistinctBy(s => s.SeasonId)];
        analyzedItems = [.. analyzedItems.DistinctBy(a => (a.ItemId, a.Type))];
        disabledItems = [.. disabledItems.DistinctBy(d => d.ItemId)];
        projectionQueue = [.. projectionQueue.DistinctBy(q => q.ItemId)];

        // Auto-increment keys must not be restored verbatim into the fresh table. The
        // external operations were read ordered by Id, so re-insertion in list order
        // preserves their FIFO semantics under fresh keys.
        foreach (var record in importRecords)
        {
            record.Id = 0;
        }

        foreach (var operation in projectionExternalOperations)
        {
            operation.Id = 0;
        }

        using var scratch = new SqliteConnection("Data Source=:memory:");
        await scratch.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var db = new IntroSkipperDbContext(new DbContextOptionsBuilder<IntroSkipperDbContext>().UseSqlite(scratch).Options))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            // Restore in bounded batches with a cleared tracker (the importer's pattern)
            // so a large library's snapshot is not also held in the change tracker all at
            // once.
            await AddInBatchesAsync(db, db.Segments, segments, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.SeasonStates, seasonStates, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.SeasonAnalysisOverrides, seasonAnalysisOverrides, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.AnalyzedItems, analyzedItems, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.DisabledItems, disabledItems, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.ImportHistory, importRecords, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.ProjectionQueue, projectionQueue, cancellationToken).ConfigureAwait(false);
            await AddInBatchesAsync(db, db.ProjectionExternalOperations, projectionExternalOperations, cancellationToken).ConfigureAwait(false);
        }

        // The copy is one SQLite write transaction on the live file, so a failure before it
        // leaves the file untouched and open connections see the new content on their next statement.
        scratch.BackupDatabase((SqliteConnection)Database.GetDbConnection());
    }

    private static async Task AddInBatchesAsync<TEntity>(
        IntroSkipperDbContext db,
        DbSet<TEntity> set,
        List<TEntity> entities,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        foreach (var batch in entities.Chunk(SaveBatchSize))
        {
            await db.SaveBatchAsync(set, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds and saves one bounded batch, then clears the change tracker so a large
    /// data set is never held tracked all at once. No-op for an empty batch.
    /// </summary>
    /// <typeparam name="TEntity">Entity type.</typeparam>
    /// <param name="set">The set to add into.</param>
    /// <param name="batch">Entities to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal async Task SaveBatchAsync<TEntity>(DbSet<TEntity> set, IReadOnlyCollection<TEntity> batch, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (batch.Count == 0)
        {
            return;
        }

        set.AddRange(batch);
        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ChangeTracker.Clear();
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
        var connectionString = Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource is not (null or "" or ":memory:") ? builder.DataSource : null;
    }
}
