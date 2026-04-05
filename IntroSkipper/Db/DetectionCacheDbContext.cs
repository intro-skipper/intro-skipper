// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IntroSkipper.Db;

/// <summary>
/// EF Core database context for the FFmpeg detection cache.
/// Stored in a separate SQLite file (<c>introskipper-cache.db</c>) so cache corruption
/// does not affect the main segment/season database.
/// </summary>
public class DetectionCacheDbContext : DbContext
{
    private readonly string? _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDbContext"/> class.
    /// </summary>
    /// <param name="dbPath">The path to the SQLite database file.</param>
    public DetectionCacheDbContext(string dbPath)
    {
        _dbPath = dbPath;
        DetectionCache = Set<DbDetectionCache>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDbContext"/> class.
    /// </summary>
    /// <param name="options">The context options.</param>
    public DetectionCacheDbContext(DbContextOptions<DetectionCacheDbContext> options) : base(options)
    {
        _dbPath = null;
        DetectionCache = Set<DbDetectionCache>();
    }

    /// <summary>
    /// Gets or sets the <see cref="DbSet{TEntity}"/> containing the detection cache entries.
    /// </summary>
    public DbSet<DbDetectionCache> DetectionCache { get; set; }

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder
                .UseSqlite($"Data Source={_dbPath}")
                .AddInterceptors(new SqlitePragmaInterceptor());
        }
    }

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

            entity.HasIndex(e => e.ItemId);
            entity.HasIndex(e => e.Mode);

            entity.Property(e => e.Start)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.End)
                  .HasDefaultValue(0.0)
                  .IsRequired();

            entity.Property(e => e.Data)
                  .IsRequired();
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Ensures the database and schema are created.
    /// Uses <c>EnsureCreated</c> rather than migrations because this is a cache database
    /// with no schema evolution requirements — it can safely be deleted and recreated.
    /// If the database file already exists but the schema does not match the current model
    /// (e.g., left over from a previous format), the database is dropped and recreated.
    /// </summary>
    public void EnsureSchema()
    {
        Database.EnsureCreated();

        if (!HasExpectedSchema())
        {
            Database.EnsureDeleted();
            Database.EnsureCreated();
        }
    }

    /// <summary>
    /// Asynchronously ensures the database and schema are created.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        if (!HasExpectedSchema())
        {
            await Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
            await Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks whether the DetectionCache table has the columns expected by the current model.
    /// Returns <c>false</c> when the database was created by an older schema version (e.g., binary BLOB format).
    /// </summary>
    private bool HasExpectedSchema()
    {
        try
        {
            using var cmd = Database.GetDbConnection().CreateCommand();
            Database.OpenConnection();
            try
            {
                cmd.CommandText = "PRAGMA table_info('DetectionCache')";
                using var reader = cmd.ExecuteReader();

                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1)); // column name is at index 1
                }

                // The current model requires at least these columns.
                return columns.Contains("Id")
                    && columns.Contains("ItemId")
                    && columns.Contains("Mode")
                    && columns.Contains("Type")
                    && columns.Contains("Start")
                    && columns.Contains("End")
                    && columns.Contains("Data");
            }
            finally
            {
                Database.CloseConnection();
            }
        }
        catch (Exception)
        {
            // If we can't read the schema, assume it's invalid and let the caller recreate.
            return false;
        }
    }

    private sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var cmd = connection.CreateCommand();
            try
            {
                SqlitePragmas.Apply(cmd);
            }
            catch (SqliteException)
            {
                // Fall back to SQLite defaults when optional pragmas such as busy_timeout cannot be applied.
            }
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            using var cmd = connection.CreateCommand();
            try
            {
                await SqlitePragmas.ApplyAsync(cmd, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Fall back to SQLite defaults when optional pragmas such as busy_timeout cannot be applied.
            }
        }
    }
}
