// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed class TestDetectionCacheDbContext : IDisposable
{
    private readonly string _dbPath;

    public TestDetectionCacheDbContext()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests");
        var fileName = $"cache-{Guid.NewGuid():N}.db";
        _dbPath = Path.Combine(baseDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        using var db = CreateContext();
        db.EnsureSchema();
    }

    public void Dispose()
    {
        DeleteDatabaseFiles();
    }

    [Fact]
    public void Write_ThenRead_ReturnsSameData()
    {
        var id = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("[1,2,3,255]");

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, payload));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var entry = db.DetectionCache.FirstOrDefault(e => e.ItemId == id && e.Type == CacheEntryType.Chromaprint);
            Assert.NotNull(entry);
            Assert.Equal(payload, entry.Data);
        }
    }

    [Fact]
    public void Query_ReturnsNull_WhenAbsent()
    {
        var absent = Guid.NewGuid();
        using var db = CreateContext();
        var entry = db.DetectionCache.FirstOrDefault(e => e.ItemId == absent);
        Assert.Null(entry);
    }

    [Fact]
    public void Write_OverwritesExistingEntry()
    {
        // Mirrors the upsert pattern in DetectionCacheService.Write:
        // find by composite key, update Data if found, else add new.
        var id = Guid.NewGuid();
        var mode = AnalysisMode.Introduction;
        var type = CacheEntryType.Chromaprint;
        var original = Encoding.UTF8.GetBytes("[1]");
        var updated = Encoding.UTF8.GetBytes("[2,3]");

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, mode, type, original));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var existing = db.DetectionCache
                .FirstOrDefault(e => e.ItemId == id && e.Mode == mode && e.Type == type && e.Start == 0 && e.End == 0);

            Assert.NotNull(existing);
            existing.Data = updated;
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var entry = db.DetectionCache.First(e => e.ItemId == id && e.Type == type);
            Assert.Equal(updated, entry.Data);
        }
    }

    [Fact]
    public void Exists_ReturnsTrueForExistingEntry()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.True(db.DetectionCache.Any(e => e.ItemId == id && e.Type == CacheEntryType.Chromaprint));
        }
    }

    [Fact]
    public void Exists_ReturnsFalse_WhenAbsent()
    {
        var absent = Guid.NewGuid();
        using var db = CreateContext();
        Assert.False(db.DetectionCache.Any(e => e.ItemId == absent));
    }

    [Fact]
    public void DeleteByEpisodeId_RemovesAllRowsForEpisode()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 0, 30));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            db.DetectionCache.RemoveRange(db.DetectionCache.Where(e => e.ItemId == id));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.False(db.DetectionCache.Any(e => e.ItemId == id));
        }
    }

    [Fact]
    public void DeleteByEpisodeId_DoesNotAffectOtherEpisodes()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id1, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id2, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            db.DetectionCache.RemoveRange(db.DetectionCache.Where(e => e.ItemId == id1));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.False(db.DetectionCache.Any(e => e.ItemId == id1));
            Assert.True(db.DetectionCache.Any(e => e.ItemId == id2));
        }
    }

    [Fact]
    public void DeleteByMode_Introduction_RemovesIntroductionRows()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            db.DetectionCache.RemoveRange(db.DetectionCache.Where(e => e.Mode == AnalysisMode.Introduction));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.False(db.DetectionCache.Any(e => e.ItemId == id && e.Mode == AnalysisMode.Introduction), "Introduction row should be deleted");
            Assert.True(db.DetectionCache.Any(e => e.ItemId == id && e.Mode == AnalysisMode.Credits), "Credits row should be kept");
        }
    }

    [Fact]
    public void DeleteByMode_Credits_RemovesCreditsRows()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            db.DetectionCache.RemoveRange(db.DetectionCache.Where(e => e.Mode == AnalysisMode.Credits));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.True(db.DetectionCache.Any(e => e.ItemId == id && e.Mode == AnalysisMode.Introduction), "Introduction row should be kept");
            Assert.False(db.DetectionCache.Any(e => e.ItemId == id && e.Mode == AnalysisMode.Credits), "Credits row should be deleted");
        }
    }

    [Fact]
    public void GetAllEpisodeIds_ReturnsDistinctIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id1, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id1, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.DetectionCache.Add(new DbDetectionCache(id2, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var ids = db.DetectionCache
                .Select(e => e.ItemId)
                .Distinct()
                .ToHashSet();

            Assert.Contains(id1, ids);
            Assert.Contains(id2, ids);
            Assert.Equal(2, ids.Count);
        }
    }

    [Fact]
    public void UniqueIndex_PreventsUpsertDuplicate()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 0, 30));
            db.SaveChanges();
        }

        // Adding same (ItemId, Mode, Type, Start, End) should throw
        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Silence, Encoding.UTF8.GetBytes("[1]"), 0, 30));
            Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void DifferentStartEnd_AllowedForSameItemAndType()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 0, 30));
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 30, 60));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.Equal(2, db.DetectionCache.Count(e => e.ItemId == id && e.Type == CacheEntryType.Silence));
        }
    }

    [Fact]
    public void EnsureSchema_RecreatesIncompatibleCacheSchema()
    {
        var dbPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, $"bad-cache-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE DetectionCache (
                        Id INTEGER NOT NULL CONSTRAINT PK_DetectionCache PRIMARY KEY AUTOINCREMENT
                    );
                    """;
                command.ExecuteNonQuery();
            }

            var id = Guid.NewGuid();
            using (var db = new DetectionCacheDbContext(dbPath))
            {
                db.EnsureSchema();
                db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
                db.SaveChanges();
            }

            using (var db = new DetectionCacheDbContext(dbPath))
            {
                Assert.True(db.DetectionCache.Any(e => e.ItemId == id));
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    private void DeleteDatabaseFiles() => DeleteDatabaseFiles(_dbPath);

    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }.Where(File.Exists))
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
                // Best-effort cleanup for test database files; ignore I/O errors on delete.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup for test database files; ignore permission issues on delete.
            }
        }
    }

    private DetectionCacheDbContext CreateContext() => new(_dbPath);
}
