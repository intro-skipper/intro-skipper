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

/// <summary>
/// Cache schema invariants and the schema lifecycle's corruption recovery.
/// </summary>
public sealed class TestDetectionCacheDbContext : IDisposable
{
    private readonly TempCacheDb _cache = new();

    public void Dispose() => _cache.Dispose();

    [Fact]
    public void UniqueIndex_PreventsUpsertDuplicate()
    {
        var id = Guid.NewGuid();

        using (var db = CreateContext())
        {
            db.EnsureSchema();
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
            db.EnsureSchema();
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
    public void TryInitialize_RecoversFromCorruptCacheFile_ByDeleteAndRecreate()
    {
        // Garbage bytes with no SQLite header: opening the file succeeds, but the
        // first statement fails with SQLITE_NOTADB. EnsureSchema must take the
        // delete-and-recreate recovery path.
        var garbage = new byte[4096];
        Array.Fill(garbage, (byte)0xDE);
        File.WriteAllBytes(_cache.Path, garbage);

        var cacheDatabase = _cache.Database;
        Assert.True(cacheDatabase.TryInitialize());

        // The recreated database must be fully operational: Upsert/FindEntry
        // round-trips. (These would throw SQLITE_NOTADB if the garbage file
        // had survived.)
        var itemId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("[1,2,3]");
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10, payload, "config-hash");

        var entry = cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10);
        Assert.NotNull(entry);
        Assert.Equal(payload, entry.Data);
        Assert.Equal("config-hash", entry.ConfigHash);
    }

    [Fact]
    public void EnsureSchema_RecreatesIncompatibleCacheSchema()
    {
        using (var connection = new SqliteConnection($"Data Source={_cache.Path}"))
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
        using (var db = CreateContext())
        {
            db.EnsureSchema();
            db.DetectionCache.Add(new DbDetectionCache(id, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.True(db.DetectionCache.Any(e => e.ItemId == id));
        }
    }

    private DetectionCacheDbContext CreateContext() => _cache.Context();
}
