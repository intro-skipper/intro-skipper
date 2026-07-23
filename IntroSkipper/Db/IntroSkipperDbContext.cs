// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IntroSkipper.Db;

/// <summary>
/// Plugin database.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
/// </remarks>
public class IntroSkipperDbContext : DbContext
{
    private const int CommercialType = (int)AnalysisMode.Commercial;

    private static readonly SqlitePragmaInterceptor _pragmaInterceptor = new();
    private static readonly string[] _currentMigrationIds =
    [
        "20241116153434_InitialCreate",
        "20260309205737_AddIsUserProvided",
        "20260314184512_AddDbSegmentIdentity",
        "20260316060001_AddNonCommercialUniqueIndex",
        "20260519073000_AddConfigHashes",
        "20260613185809_ReplaceSeasonInfoWithSeasonState",
        "20260723120000_AddDisabledEpisodes"
    ];

    private readonly string? _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDbContext"/> class.
    /// </summary>
    /// <param name="dbPath">The path to the SQLite database file.</param>
    public IntroSkipperDbContext(string dbPath)
    {
        _dbPath = dbPath;
        DbSegment = Set<DbSegment>();
        DbSeasonState = Set<DbSeasonState>();
        DbDisabledEpisode = Set<DbDisabledEpisode>();
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
        DbSegment = Set<DbSegment>();
        DbSeasonState = Set<DbSeasonState>();
        DbDisabledEpisode = Set<DbDisabledEpisode>();
    }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the segments.
    /// </summary>
    public DbSet<DbSegment> DbSegment { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the season state.
    /// </summary>
    public DbSet<DbSeasonState> DbSeasonState { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing explicitly disabled episodes.
    /// </summary>
    public DbSet<DbDisabledEpisode> DbDisabledEpisode { get; set; }

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
            entity.ToTable("DbSegment");
            entity.HasKey(s => s.Id);

            entity.Property(e => e.Id)
                  .ValueGeneratedOnAdd();

            entity.HasIndex(e => e.ItemId);
            entity.HasIndex(e => new { e.ItemId, e.Type, e.Start, e.End })
                .HasDatabaseName("IX_DbSegment_Commercial_Unique")
                .HasFilter($"Type = {CommercialType}")
                .IsUnique();
            entity.HasIndex(e => new { e.ItemId, e.Type })
                .HasDatabaseName("IX_DbSegment_NonCommercial_Unique")
                .HasFilter($"Type != {CommercialType}")
                .IsUnique();

            entity.Property(e => e.Start)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.End)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.IsUserProvided)
                  .HasDefaultValue(false)
                  .IsRequired();

            entity.Property(e => e.ConfigHash)
                  .HasDefaultValue(string.Empty)
                  .IsRequired();
        });

        modelBuilder.Entity<DbSeasonState>(entity =>
        {
            entity.ToTable("DbSeasonState");
            entity.HasKey(s => new { s.SeasonId, s.Type });

            entity.HasIndex(e => e.SeasonId);

            entity.Property(e => e.Action)
                  .HasDefaultValue(AnalyzerAction.Default)
                  .IsRequired();

            entity.Property(e => e.ConfigHash)
                  .HasDefaultValue(string.Empty)
                  .IsRequired();

            entity.Property(e => e.SettledReanalysisEpisodeIds)
                  .HasDefaultValueSql("'[]'")
                  .IsRequired();
        });

        modelBuilder.Entity<DbDisabledEpisode>(entity =>
        {
            entity.ToTable("DbDisabledEpisode");
            entity.HasKey(e => e.EpisodeId);
            entity.HasIndex(e => e.SeasonId);
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
    /// Ensures legacy databases contain the ConfigHash columns expected by the current model.
    /// </summary>
    public void EnsureConfigHashColumns()
    {
        EnsureLegacySchemaCompatibility();
    }

    /// <summary>
    /// Ensures legacy databases have the current schema shape without dropping existing data.
    /// </summary>
    public void EnsureLegacySchemaCompatibility()
    {
        var connection = Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            Database.OpenConnection();
        }

        try
        {
            using var transaction = Database.BeginTransaction();
            EnsureDbSegmentSchema();
            EnsureDbSeasonStateSchema();
            EnsureDbDisabledEpisodeSchema();
            EnsureMigrationHistoryForCurrentSchema();
            transaction.Commit();
        }
        finally
        {
            if (!wasOpen)
            {
                Database.CloseConnection();
            }
        }
    }

    /// <summary>
    /// Asynchronously rebuilds the database while attempting to preserve valid segments and season state.
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
        var disabledEpisodes = new List<DbDisabledEpisode>();
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
                segments = await db.DbSegment.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                segments = [.. segments.Where(s => s.ToSegment().Valid)];
                seasonStates = await db.DbSeasonState.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                disabledEpisodes = await db.DbDisabledEpisode.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
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

        // Restore whatever data was salvaged
        if (segments.Count > 0 || seasonStates.Count > 0 || disabledEpisodes.Count > 0)
        {
            using var db = contextFactory();
            if (segments.Count > 0)
            {
                db.DbSegment.AddRange(segments);
            }

            if (seasonStates.Count > 0)
            {
                db.DbSeasonState.AddRange(seasonStates);
            }

            if (disabledEpisodes.Count > 0)
            {
                db.DbDisabledEpisode.AddRange(disabledEpisodes);
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

    private void EnsureConfigHashColumn(string tableName)
    {
        if (!TableExists(tableName) || ColumnExists(tableName, "ConfigHash"))
        {
            return;
        }

        switch (tableName)
        {
            case "DbSegment":
                Database.ExecuteSqlRaw("ALTER TABLE \"DbSegment\" ADD COLUMN \"ConfigHash\" TEXT NOT NULL DEFAULT ''");
                break;
            case "DbSeasonInfo":
                Database.ExecuteSqlRaw("ALTER TABLE \"DbSeasonInfo\" ADD COLUMN \"ConfigHash\" TEXT NOT NULL DEFAULT ''");
                break;
            default:
                throw new InvalidOperationException($"Unsupported table '{tableName}'.");
        }
    }

    private void EnsureDbSegmentSchema()
    {
        if (!TableExists("DbSegment"))
        {
            return;
        }

        if (!ColumnExists("DbSegment", "IsUserProvided"))
        {
            Database.ExecuteSqlRaw("ALTER TABLE \"DbSegment\" ADD COLUMN \"IsUserProvided\" INTEGER NOT NULL DEFAULT 0");
        }

        EnsureConfigHashColumn("DbSegment");

        if (!ColumnExists("DbSegment", "Id"))
        {
            RebuildDbSegmentWithIdentity();
        }

        EnsureDbSegmentIndexes();
    }

    private void EnsureDbSeasonStateSchema()
    {
        if (TableExists("DbSeasonState"))
        {
            Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_DbSeasonState_SeasonId\" ON \"DbSeasonState\" (\"SeasonId\")");

            if (!ColumnExists("DbSeasonState", "SettledReanalysisEpisodeIds"))
            {
                Database.ExecuteSqlRaw("ALTER TABLE \"DbSeasonState\" ADD COLUMN \"SettledReanalysisEpisodeIds\" TEXT NOT NULL DEFAULT '[]'");
            }

            return;
        }

        if (!TableExists("DbSeasonInfo"))
        {
            return;
        }

        EnsureConfigHashColumn("DbSeasonInfo");
        Database.ExecuteSqlRaw(
            """
            CREATE TABLE "DbSeasonState" (
                "SeasonId" TEXT NOT NULL,
                "Type" INTEGER NOT NULL,
                "Action" INTEGER NOT NULL DEFAULT 0,
                "EpisodeIds" TEXT NOT NULL,
                "ConfigHash" TEXT NOT NULL DEFAULT '',
                "SettledReanalysisEpisodeIds" TEXT NOT NULL DEFAULT '[]',
                CONSTRAINT "PK_DbSeasonState" PRIMARY KEY ("SeasonId", "Type")
            )
            """);

        Database.ExecuteSqlRaw(
            """
            INSERT INTO "DbSeasonState" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash", "SettledReanalysisEpisodeIds")
            SELECT "SeasonId", "Type", "Action", "EpisodeIds", COALESCE("ConfigHash", ''), '[]'
            FROM "DbSeasonInfo"
            """);

        Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_DbSeasonState_SeasonId\" ON \"DbSeasonState\" (\"SeasonId\")");
        Database.ExecuteSqlRaw("DROP TABLE \"DbSeasonInfo\"");
    }

    private void EnsureDbDisabledEpisodeSchema()
    {
        if (TableExists("DbDisabledEpisode") || !TableExists("DbSeasonState"))
        {
            return;
        }

        Database.ExecuteSqlRaw(
            """
            CREATE TABLE "DbDisabledEpisode" (
                "EpisodeId" TEXT NOT NULL,
                "SeasonId" TEXT NOT NULL,
                CONSTRAINT "PK_DbDisabledEpisode" PRIMARY KEY ("EpisodeId")
            )
            """);
        Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_DbDisabledEpisode_SeasonId\" ON \"DbDisabledEpisode\" (\"SeasonId\")");
    }

    private void RebuildDbSegmentWithIdentity()
    {
        Database.ExecuteSqlRaw(
            """
            CREATE TABLE "DbSegment_Temp" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_DbSegment_Temp" PRIMARY KEY AUTOINCREMENT,
                "ItemId" TEXT NOT NULL,
                "Type" INTEGER NOT NULL,
                "Start" REAL NOT NULL DEFAULT 0.0,
                "End" REAL NOT NULL DEFAULT 0.0,
                "IsUserProvided" INTEGER NOT NULL DEFAULT 0,
                "ConfigHash" TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO "DbSegment_Temp" ("ItemId", "Type", "Start", "End", "IsUserProvided", "ConfigHash")
            SELECT "ItemId", "Type", "Start", "End", COALESCE("IsUserProvided", 0), COALESCE("ConfigHash", '')
            FROM "DbSegment";
            DROP TABLE "DbSegment";
            ALTER TABLE "DbSegment_Temp" RENAME TO "DbSegment";
            """);
    }

    private void EnsureDbSegmentIndexes()
    {
        Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_DbSegment_ItemId\" ON \"DbSegment\" (\"ItemId\")");
        Database.ExecuteSqlRaw(
            $$"""
            DELETE FROM "DbSegment"
            WHERE "Type" = {{CommercialType}}
            AND "Id" NOT IN (
                SELECT MAX("Id")
                FROM "DbSegment"
                WHERE "Type" = {{CommercialType}}
                GROUP BY "ItemId", "Type", "Start", "End"
            )
            """);

        Database.ExecuteSqlRaw(
            $$"""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_DbSegment_Commercial_Unique" ON "DbSegment" ("ItemId", "Type", "Start", "End")
                WHERE "Type" = {{CommercialType}}
            """);

        Database.ExecuteSqlRaw(
            $$"""
            DELETE FROM "DbSegment"
            WHERE "Type" != {{CommercialType}}
            AND "Id" NOT IN (
                SELECT MAX("Id")
                FROM "DbSegment"
                WHERE "Type" != {{CommercialType}}
                GROUP BY "ItemId", "Type"
            )
            """);

        Database.ExecuteSqlRaw(
            $$"""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_DbSegment_NonCommercial_Unique" ON "DbSegment" ("ItemId", "Type")
                WHERE "Type" != {{CommercialType}}
            """);
    }

    private void EnsureMigrationHistoryForCurrentSchema()
    {
        if (!CurrentSchemaExists())
        {
            return;
        }

        Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            )
            """);

        foreach (var migrationId in _currentMigrationIds)
        {
            InsertMigrationHistoryRecord(migrationId);
        }
    }

    private bool CurrentSchemaExists()
    {
        return TableExists("DbSegment")
            && ColumnExists("DbSegment", "Id")
            && ColumnExists("DbSegment", "ItemId")
            && ColumnExists("DbSegment", "Type")
            && ColumnExists("DbSegment", "Start")
            && ColumnExists("DbSegment", "End")
            && ColumnExists("DbSegment", "IsUserProvided")
            && ColumnExists("DbSegment", "ConfigHash")
            && IndexExists("DbSegment", "IX_DbSegment_ItemId")
            && IndexExists("DbSegment", "IX_DbSegment_Commercial_Unique")
            && IndexExists("DbSegment", "IX_DbSegment_NonCommercial_Unique")
            && TableExists("DbSeasonState")
            && ColumnExists("DbSeasonState", "SeasonId")
            && ColumnExists("DbSeasonState", "Type")
            && ColumnExists("DbSeasonState", "Action")
            && ColumnExists("DbSeasonState", "EpisodeIds")
            && ColumnExists("DbSeasonState", "ConfigHash")
            && ColumnExists("DbSeasonState", "SettledReanalysisEpisodeIds")
            && IndexExists("DbSeasonState", "IX_DbSeasonState_SeasonId");
    }

    private void InsertMigrationHistoryRecord(string migrationId)
    {
        using var command = Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ($migrationId, $productVersion)
            """;
        command.Transaction = Database.CurrentTransaction?.GetDbTransaction();

        var migrationParameter = command.CreateParameter();
        migrationParameter.ParameterName = "$migrationId";
        migrationParameter.Value = migrationId;
        command.Parameters.Add(migrationParameter);

        var productVersionParameter = command.CreateParameter();
        productVersionParameter.ParameterName = "$productVersion";
        productVersionParameter.Value = "9.0.11";
        command.Parameters.Add(productVersionParameter);

        command.ExecuteNonQuery();
    }

    private bool IndexExists(string tableName, string indexName)
    {
        using var command = Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND tbl_name = $tableName AND name = $indexName LIMIT 1";
        command.Transaction = Database.CurrentTransaction?.GetDbTransaction();

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "$tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        var indexParameter = command.CreateParameter();
        indexParameter.ParameterName = "$indexName";
        indexParameter.Value = indexName;
        command.Parameters.Add(indexParameter);

        return command.ExecuteScalar() is not null;
    }

    private bool TableExists(string tableName)
    {
        using var command = Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Transaction = Database.CurrentTransaction?.GetDbTransaction();

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        return command.ExecuteScalar() is not null;
    }

    private bool ColumnExists(string tableName, string columnName)
    {
        using var command = Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($tableName) WHERE name = $columnName COLLATE NOCASE LIMIT 1";
        command.Transaction = Database.CurrentTransaction?.GetDbTransaction();

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "$tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "$columnName";
        columnParameter.Value = columnName;
        command.Parameters.Add(columnParameter);

        return command.ExecuteScalar() is not null;
    }
}
