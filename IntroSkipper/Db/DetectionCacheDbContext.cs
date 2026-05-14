// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// EF Core database context for the FFmpeg detection cache.
/// Stored in a separate SQLite file (<c>introskipper-cache.db</c>) so cache corruption
/// does not affect the main segment/season database.
/// </summary>
public class DetectionCacheDbContext : DbContext
{
    private static readonly SqlitePragmaInterceptor _pragmaInterceptor = new();
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
                .AddInterceptors(_pragmaInterceptor);
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
    /// Uses <c>EnsureCreated</c> rather than migrations because this is a disposable cache database.
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

    private bool HasExpectedSchema()
    {
        try
        {
            Database.OpenConnection();
            try
            {
                return HasExpectedColumns() && HasExpectedUniqueIndex();
            }
            finally
            {
                Database.CloseConnection();
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool HasExpectedColumns()
    {
        using var cmd = Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "PRAGMA table_info('DetectionCache')";

        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                columns[reader.GetString(1)] = reader.GetString(2);
            }
        }

        return HasColumn("Id", "INTEGER") &&
            HasColumn("ItemId", "TEXT") &&
            HasColumn("Mode", "INTEGER") &&
            HasColumn("Type", "INTEGER") &&
            HasColumn("Start", "REAL") &&
            HasColumn("End", "REAL") &&
            HasColumn("Data", "BLOB");

        bool HasColumn(string name, string type)
            => columns.TryGetValue(name, out var actualType) &&
                string.Equals(actualType, type, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasExpectedUniqueIndex()
    {
        using var cmd = Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "PRAGMA index_info('IX_DetectionCache_Unique')";

        using var reader = cmd.ExecuteReader();
        return reader.HasRows;
    }
}
