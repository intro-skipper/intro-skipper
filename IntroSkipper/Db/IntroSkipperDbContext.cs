// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace IntroSkipper.Db;

/// <summary>
/// Plugin segment database (<c>introskipper-v2.db</c>). The schema is owned by EF
/// migrations (a single baseline; later changes are plain migrations on top); data from
/// the legacy <c>introskipper.db</c> is carried over once by <see cref="LegacyDatabaseImporter"/>.
/// </summary>
public class IntroSkipperDbContext : DbContext
{
    // SQLite stores DateTime without a kind; every stored timestamp is UTC, so reads
    // must come back marked as such or comparisons silently use local time.
    private static readonly ValueConverter<DateTime, DateTime> _utcDateTimeConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

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
        AnalyzedItems = Set<DbAnalyzedItem>();
        ImportHistory = Set<DbImportRecord>();
        DisabledItems = Set<DbDisabledItem>();
        ProjectionPlans = Set<DbProjectionPlan>();
        ProjectionPlanSegments = Set<DbProjectionPlanSegment>();
        ProjectionExternalOperations = Set<DbProjectionExternalOperation>();
        ProjectionAttempts = Set<DbProjectionAttempt>();
        ProjectionHeads = Set<DbProjectionHead>();
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
        AnalyzedItems = Set<DbAnalyzedItem>();
        ImportHistory = Set<DbImportRecord>();
        DisabledItems = Set<DbDisabledItem>();
        ProjectionPlans = Set<DbProjectionPlan>();
        ProjectionPlanSegments = Set<DbProjectionPlanSegment>();
        ProjectionExternalOperations = Set<DbProjectionExternalOperation>();
        ProjectionAttempts = Set<DbProjectionAttempt>();
        ProjectionHeads = Set<DbProjectionHead>();
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
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the per-item analysis records.
    /// </summary>
    public DbSet<DbAnalyzedItem> AnalyzedItems { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the legacy-import markers.
    /// </summary>
    public DbSet<DbImportRecord> ImportHistory { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the items whose automatic
    /// segments are withheld from Jellyfin.
    /// </summary>
    public DbSet<DbDisabledItem> DisabledItems { get; set; }

    /// <summary>Gets or sets immutable projection plan headers.</summary>
    internal DbSet<DbProjectionPlan> ProjectionPlans { get; set; }

    /// <summary>Gets or sets immutable projection plan segment images.</summary>
    internal DbSet<DbProjectionPlanSegment> ProjectionPlanSegments { get; set; }

    /// <summary>Gets or sets immutable exact external operations.</summary>
    internal DbSet<DbProjectionExternalOperation> ProjectionExternalOperations { get; set; }

    /// <summary>Gets or sets mutable projection retry attempts.</summary>
    internal DbSet<DbProjectionAttempt> ProjectionAttempts { get; set; }

    /// <summary>Gets or sets compacted per-item projection heads.</summary>
    internal DbSet<DbProjectionHead> ProjectionHeads { get; set; }

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            SqlitePragmas.Configure(optionsBuilder, _dbPath!);
        }
    }

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

            // One flag per item by construction; the SeasonId index serves the
            // per-season listing (cleanup prunes by item ID).
            entity.HasKey(e => e.ItemId);
            entity.HasIndex(e => e.SeasonId);
        });

        modelBuilder.Entity<DbProjectionPlan>(entity =>
        {
            entity.ToTable("ProjectionPlans");
            entity.HasKey(e => new { e.ChangeId, e.ItemId });
            entity.HasIndex(e => new { e.ItemId, e.Sequence }).IsUnique();
            entity.Property(e => e.CreatedAt).HasConversion(_utcDateTimeConverter);
        });

        modelBuilder.Entity<DbProjectionPlanSegment>(entity =>
        {
            entity.ToTable("ProjectionPlanSegments");
            entity.HasKey(e => new { e.ChangeId, e.ItemId, e.Position });
        });

        modelBuilder.Entity<DbProjectionExternalOperation>(entity =>
        {
            entity.ToTable("ProjectionExternalOperations");
            entity.HasKey(e => new { e.ChangeId, e.ItemId, e.Position });
            entity.HasIndex(e => new { e.ItemId, e.ExternalSegmentId });
        });

        modelBuilder.Entity<DbProjectionAttempt>(entity =>
        {
            entity.ToTable("ProjectionAttempts");
            entity.HasKey(e => new { e.ChangeId, e.ItemId });
            entity.Property(e => e.LastAttemptAt).HasConversion(_utcDateTimeConverter);
            entity.Property(e => e.NextAttemptAt).HasConversion(_utcDateTimeConverter);
        });

        modelBuilder.Entity<DbProjectionHead>(entity =>
        {
            entity.ToTable("ProjectionHeads");
            entity.HasKey(e => e.ItemId);
        });

        base.OnModelCreating(modelBuilder);
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
    /// Asynchronously rebuilds the database while attempting to preserve segments,
    /// season state, analysis records, disabled items and the legacy-import marker. When no marker exists
    /// (the legacy import never succeeded) none is written, so the next start retries the
    /// import; the importer skips rows the restored data already holds.
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
        var analyzedItems = new List<DbAnalyzedItem>();
        var importRecords = new List<DbImportRecord>();
        var disabledItems = new List<DbDisabledItem>();
        var projectionPlans = new List<DbProjectionPlan>();
        var projectionPlanSegments = new List<DbProjectionPlanSegment>();
        var projectionExternalOperations = new List<DbProjectionExternalOperation>();
        var projectionAttempts = new List<DbProjectionAttempt>();
        var projectionHeads = new List<DbProjectionHead>();
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
                // Suppressed rows are salvaged too: tombstones are user intent.
                segments = await db.Segments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                seasonStates = await db.SeasonStates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                analyzedItems = await db.AnalyzedItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                importRecords = await db.ImportHistory.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                disabledItems = await db.DisabledItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                projectionPlans = await db.ProjectionPlans.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                projectionPlanSegments = await db.ProjectionPlanSegments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                projectionExternalOperations = await db.ProjectionExternalOperations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                projectionAttempts = await db.ProjectionAttempts.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                projectionHeads = await db.ProjectionHeads.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
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

        // Auto-increment keys must not be restored verbatim into the fresh table.
        foreach (var record in importRecords)
        {
            record.Id = 0;
        }

        using (var db = contextFactory())
        {
            // Restore in bounded batches with a cleared tracker (the importer's pattern)
            // so a large library's snapshot is not also held in the change tracker all at
            // once; the explicit transaction keeps the restore all-or-nothing.
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await AddInBatchesAsync(db, db.Segments, segments, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.SeasonStates, seasonStates, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.AnalyzedItems, analyzedItems, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.DisabledItems, disabledItems, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.ProjectionPlans, projectionPlans, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.ProjectionPlanSegments, projectionPlanSegments, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.ProjectionExternalOperations, projectionExternalOperations, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.ProjectionAttempts, projectionAttempts, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.ProjectionHeads, projectionHeads, cancellationToken).ConfigureAwait(false);
                await AddInBatchesAsync(db, db.ImportHistory, importRecords, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddInBatchesAsync<TEntity>(
        IntroSkipperDbContext db,
        DbSet<TEntity> set,
        List<TEntity> entities,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        foreach (var batch in entities.Chunk(1000))
        {
            set.AddRange(batch);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
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
