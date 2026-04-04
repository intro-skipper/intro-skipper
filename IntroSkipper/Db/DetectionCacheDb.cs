// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Lightweight SQLite key-value store for FFmpeg detection cache blobs.
/// Uses Microsoft.Data.Sqlite directly; no EF Core overhead.
/// Each method opens a short-lived connection, consistent with <see cref="IntroSkipperDbContext"/> usage patterns.
/// Implements <see cref="IDisposable"/> so callers can use <c>using var db = Plugin.CreateCacheDb()</c>;
/// the actual disposal is a no-op because no long-lived resources are held.
/// </summary>
public sealed partial class DetectionCacheDb : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDb"/> class.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    /// <param name="logger">Optional logger.</param>
    public DetectionCacheDb(string dbPath, ILogger? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    /// <summary>
    /// Creates the <c>DetectionCache</c> table and index if they do not already exist.
    /// Call once at plugin startup before any read/write operations.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    public static void EnsureSchema(string dbPath)
    {
        using var connection = OpenConnection(dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS DetectionCache (CacheKey TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Tries to read a cache entry by key.
    /// </summary>
    /// <param name="cacheKey">The cache key (previously the filename on disk).</param>
    /// <param name="data">The raw binary payload if found.</param>
    /// <returns><c>true</c> if the key was found; otherwise <c>false</c>.</returns>
    public bool TryRead(string cacheKey, out byte[] data)
    {
        data = [];
        try
        {
            using var connection = OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Data FROM DetectionCache WHERE CacheKey = $key";
            cmd.Parameters.AddWithValue("$key", cacheKey);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            data = (byte[])reader["Data"];
            return true;
        }
        catch (SqliteException ex)
        {
            if (_logger is { } logger)
            {
                LogReadError(logger, ex, cacheKey);
            }

            return false;
        }
    }

    /// <summary>
    /// Writes (or overwrites) a cache entry.
    /// </summary>
    /// <param name="cacheKey">The cache key.</param>
    /// <param name="data">The raw binary payload to store.</param>
    public void Write(string cacheKey, byte[] data)
    {
        try
        {
            using var connection = OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO DetectionCache (CacheKey, Data) VALUES ($key, $data)";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            cmd.Parameters.AddWithValue("$data", data);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            if (_logger is { } logger)
            {
                LogWriteError(logger, ex, cacheKey);
            }
        }
    }

    /// <summary>
    /// Deletes all cache entries whose key starts with the given episode ID prefix.
    /// </summary>
    /// <param name="episodeId">The episode GUID.</param>
    public void DeleteByEpisodeId(Guid episodeId)
    {
        try
        {
            using var connection = OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM DetectionCache WHERE CacheKey LIKE $prefix";
            cmd.Parameters.AddWithValue("$prefix", episodeId.ToString("N") + "%");
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            if (_logger is { } logger)
            {
                LogDeleteError(logger, ex, episodeId.ToString("N"));
            }
        }
    }

    /// <summary>
    /// Deletes cache entries matching the given analysis mode.
    /// Introduction mode deletes all non-credits entries; Credits mode deletes all credits entries.
    /// </summary>
    /// <param name="mode">The analysis mode to delete cache for.</param>
    public void DeleteByMode(AnalysisMode mode)
    {
        const string DeleteIntroSql = "DELETE FROM DetectionCache WHERE instr(lower(CacheKey), '-credits') = 0";
        const string DeleteCreditsSql = "DELETE FROM DetectionCache WHERE instr(lower(CacheKey), '-credits') > 0";

        try
        {
            using var connection = OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
#pragma warning disable CA2100 // SQL comes from compile-time constants, not user input.
            cmd.CommandText = mode == AnalysisMode.Introduction ? DeleteIntroSql : DeleteCreditsSql;
#pragma warning restore CA2100
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            if (_logger is { } logger)
            {
                LogDeleteModeError(logger, ex, mode);
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> if a cache entry with the given key exists.
    /// </summary>
    /// <param name="cacheKey">The cache key to check.</param>
    /// <returns><c>true</c> if the key exists in the database.</returns>
    public bool ExistsByKey(string cacheKey)
    {
        try
        {
            using var connection = OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM DetectionCache WHERE CacheKey = $key)";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException ex)
        {
            if (_logger is { } logger)
            {
                LogReadError(logger, ex, cacheKey);
            }

            return false;
        }
    }

    /// <summary>
    /// Returns the distinct episode GUIDs of all entries currently stored in the cache.
    /// </summary>
    /// <returns>An enumerable of episode GUIDs.</returns>
    public IEnumerable<Guid> GetAllEpisodeIds()
    {
        var ids = new List<Guid>();
        try
        {
            using var connection = OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();

            // First 32 chars of each CacheKey are the guid formatted as "N" (no hyphens).
            cmd.CommandText = "SELECT DISTINCT substr(CacheKey, 1, 32) FROM DetectionCache";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetString(0);
                if (Guid.TryParseExact(raw, "N", out var id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (SqliteException ex)
        {
            if (_logger is { } logger)
            {
                LogReadAllError(logger, ex);
            }
        }

        return ids;
    }

    /// <summary>
    /// No-op disposal. No long-lived resources are held; each method opens its own connection.
    /// Provided so callers can use <c>using var db = Plugin.CreateCacheDb()</c>.
    /// </summary>
    public void Dispose()
    {
        // No resources to release.
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        connection.Open();

        // Set busy_timeout first so any subsequent PRAGMA (including journal_mode) retries on SQLITE_BUSY.
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout=5000;";
        pragmaCmd.ExecuteNonQuery();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
        pragmaCmd.ExecuteNonQuery();

        return connection;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read detection cache entry '{CacheKey}'")]
    private static partial void LogReadError(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write detection cache entry '{CacheKey}'")]
    private static partial void LogWriteError(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete detection cache entries for episode '{EpisodeId}'")]
    private static partial void LogDeleteError(ILogger logger, Exception ex, string episodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete detection cache entries for mode {Mode}")]
    private static partial void LogDeleteModeError(ILogger logger, Exception ex, AnalysisMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to enumerate detection cache episode IDs")]
    private static partial void LogReadAllError(ILogger logger, Exception ex);
}
