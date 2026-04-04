// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;

public sealed class TestDetectionCacheDb : IDisposable
{
    private readonly string _dbPath;

    public TestDetectionCacheDb()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests", $"cache-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        DetectionCacheDb.EnsureSchema(_dbPath);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(f))
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
    }

    [Fact]
    public void Write_ThenRead_ReturnsSameBlob()
    {
        var db = new DetectionCacheDb(_dbPath);
        byte[] payload = [0x01, 0x02, 0x03, 0xFF];

        db.Write("test-key", payload);

        Assert.True(db.TryRead("test-key", out var result));
        Assert.Equal(payload, result);
    }

    [Fact]
    public void TryRead_ReturnsFalse_WhenKeyAbsent()
    {
        var db = new DetectionCacheDb(_dbPath);

        Assert.False(db.TryRead("nonexistent-key", out var result));
        Assert.Empty(result);
    }

    [Fact]
    public void Write_OverwritesExistingKey()
    {
        var db = new DetectionCacheDb(_dbPath);
        db.Write("overwrite-key", [0x01]);
        db.Write("overwrite-key", [0x02, 0x03]);

        Assert.True(db.TryRead("overwrite-key", out var result));
        Assert.Equal(new byte[] { 0x02, 0x03 }, result);
    }

    [Fact]
    public void ExistsByKey_ReturnsTrueForExistingKey()
    {
        var db = new DetectionCacheDb(_dbPath);
        db.Write("exists-key", [0xAA]);

        Assert.True(db.ExistsByKey("exists-key"));
    }

    [Fact]
    public void ExistsByKey_ReturnsFalse_WhenKeyAbsent()
    {
        var db = new DetectionCacheDb(_dbPath);

        Assert.False(db.ExistsByKey("missing-key"));
    }

    [Fact]
    public void DeleteByEpisodeId_RemovesAllRowsForEpisode()
    {
        var db = new DetectionCacheDb(_dbPath);
        var id = Guid.NewGuid();
        var prefix = id.ToString("N");

        db.Write(prefix + "-chromaprint-v1", [0x01]);
        db.Write(prefix + "-credits-chromaprint-v1", [0x02]);
        db.Write(prefix + "-silence-0-30-v3", [0x03]);

        db.DeleteByEpisodeId(id);

        Assert.False(db.ExistsByKey(prefix + "-chromaprint-v1"));
        Assert.False(db.ExistsByKey(prefix + "-credits-chromaprint-v1"));
        Assert.False(db.ExistsByKey(prefix + "-silence-0-30-v3"));
    }

    [Fact]
    public void DeleteByEpisodeId_DoesNotAffectOtherEpisodes()
    {
        var db = new DetectionCacheDb(_dbPath);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        db.Write(id1.ToString("N") + "-chromaprint-v1", [0x01]);
        db.Write(id2.ToString("N") + "-chromaprint-v1", [0x02]);

        db.DeleteByEpisodeId(id1);

        Assert.False(db.ExistsByKey(id1.ToString("N") + "-chromaprint-v1"));
        Assert.True(db.ExistsByKey(id2.ToString("N") + "-chromaprint-v1"));
    }

    [Fact]
    public void DeleteByMode_Introduction_RemovesNonCreditsRows()
    {
        var db = new DetectionCacheDb(_dbPath);
        var prefix = Guid.NewGuid().ToString("N");

        var introKey = prefix + "-chromaprint-v1";
        var creditsKey = prefix + "-credits-chromaprint-v1";

        db.Write(introKey, [0x01]);
        db.Write(creditsKey, [0x02]);

        db.DeleteByMode(AnalysisMode.Introduction);

        Assert.False(db.ExistsByKey(introKey), "Intro row should be deleted");
        Assert.True(db.ExistsByKey(creditsKey), "Credits row should be kept");
    }

    [Fact]
    public void DeleteByMode_Credits_RemovesCreditsRows()
    {
        var db = new DetectionCacheDb(_dbPath);
        var prefix = Guid.NewGuid().ToString("N");

        var introKey = prefix + "-chromaprint-v1";
        var creditsKey = prefix + "-credits-chromaprint-v1";

        db.Write(introKey, [0x01]);
        db.Write(creditsKey, [0x02]);

        db.DeleteByMode(AnalysisMode.Credits);

        Assert.True(db.ExistsByKey(introKey), "Intro row should be kept");
        Assert.False(db.ExistsByKey(creditsKey), "Credits row should be deleted");
    }

    [Fact]
    public void GetAllEpisodeIds_ReturnsDistinctIds()
    {
        var db = new DetectionCacheDb(_dbPath);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        db.Write(id1.ToString("N") + "-chromaprint-v1", [0x01]);
        db.Write(id1.ToString("N") + "-credits-chromaprint-v1", [0x02]);
        db.Write(id2.ToString("N") + "-chromaprint-v1", [0x03]);

        var ids = db.GetAllEpisodeIds().ToHashSet();

        Assert.Contains(id1, ids);
        Assert.Contains(id2, ids);
        Assert.Equal(2, ids.Count);
    }
}
