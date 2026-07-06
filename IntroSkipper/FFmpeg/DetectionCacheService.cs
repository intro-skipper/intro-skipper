// SPDX-FileCopyrightText: 2024-2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Manages reading and writing detection results to/from the SQLite cache.
/// Serialization, compression and configuration-hash policy live here; all database
/// access is delegated to <see cref="IDetectionCacheDatabase"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DetectionCacheService"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
/// <param name="cacheDatabase">The detection cache database facade.</param>
public sealed partial class DetectionCacheService(ILogger<DetectionCacheService> logger, IDetectionCacheDatabase cacheDatabase) : IDetectionCacheService
{
    private readonly ILogger<DetectionCacheService> _logger = logger;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;

    /// <inheritdoc/>
    public bool IsEnabled => Plugin.Instance?.Configuration.CacheFingerprints ?? false;

    /// <inheritdoc/>
    public bool TryRead<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, out T[] result)
    {
        result = [];

        if (!IsEnabled)
        {
            return false;
        }

        try
        {
            // NOTE: Start/End are compared with == which is safe only because the exact same
            // double values that were written are used for lookup (no intermediate arithmetic).
            // If a future caller computes start/end differently, the lookup will silently miss.
            var entry = _cacheDatabase.FindEntry(itemId, mode, type, start, end);

            if (entry is null)
            {
                return false;
            }

            var expectedHash = ConfigHasher.DetectionCache(Plugin.Instance?.Configuration ?? new(), type, mode);
            if (!string.IsNullOrEmpty(entry.ConfigHash)
                && !string.Equals(entry.ConfigHash, expectedHash, StringComparison.Ordinal))
            {
                return false;
            }

            var json = DecompressBrotli<T[]>(entry.Data);
            result = json ?? [];

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                var cacheKey = $"{itemId:N}-{mode}-{type}";
                LogDetectionCacheHit(_logger, cacheKey);
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidDataException or DbException)
        {
            LogDetectionCacheReadError(_logger, ex, $"{itemId:N}-{mode}-{type}");
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Write<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, T[] items)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var data = CompressBrotli(items);
        var configHash = ConfigHasher.DetectionCache(Plugin.Instance?.Configuration ?? new(), type, mode);

        try
        {
            _cacheDatabase.Upsert(itemId, mode, type, start, end, data, configHash);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var cacheKey = $"{itemId:N}-{mode}-{type}";
                LogDetectionCacheWriteError(_logger, ex, cacheKey);
            }

            // Suppress duplicate-insert races and database-level cache failures. The cache is a
            // performance optimization; write failures should never discard valid analysis results.
            return false;
        }
    }

    /// <inheritdoc/>
    public void DeleteForItem(Guid itemId)
    {
        try
        {
            // Delete from the SQLite cache database.
            _cacheDatabase.DeleteForItem(itemId);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var cacheKey = itemId.ToString("N");
                LogDetectionCacheDeleteError(_logger, ex, cacheKey);
            }
        }
    }

    /// <inheritdoc/>
    public void DeleteByMode(AnalysisMode mode)
    {
        try
        {
            // Delete from the SQLite cache database.
            _cacheDatabase.DeleteByMode(mode);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var cacheKey = mode.ToString();
                LogDetectionCacheDeleteError(_logger, ex, cacheKey);
            }
        }
    }

    /// <inheritdoc/>
    public bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var (start, end) = episode.GetFingerprintRange(mode);

        try
        {
            var expectedHash = ConfigHasher.DetectionCache(Plugin.Instance?.Configuration ?? new(), CacheEntryType.Chromaprint, mode);
            if (_cacheDatabase.HasEntry(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, expectedHash))
            {
                return true;
            }
        }
        catch (DbException ex)
        {
            LogDetectionCacheReadError(_logger, ex, $"{episode.EpisodeId:N}-{mode}-{CacheEntryType.Chromaprint}");
        }

        return false;
    }

    /// <summary>
    /// Serializes and compresses a value using Brotli compression.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize and compress.</param>
    /// <returns>The Brotli-compressed data.</returns>
    public static byte[] CompressBrotli<T>(T value)
    {
        var level = Plugin.Instance?.Configuration.CacheCompressionLevel ?? CompressionLevel.Optimal;
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, level))
        {
            JsonSerializer.Serialize(brotli, value);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses and deserializes Brotli-compressed data.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="compressed">The Brotli-compressed data.</param>
    /// <returns>The deserialized value, or the default if deserialization returns null.</returns>
    public static T? DecompressBrotli<T>(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(brotli);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detection cache hit for {CacheKey}")]
    private static partial void LogDetectionCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading detection cache from {Path}")]
    private static partial void LogDetectionCacheReadError(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error writing detection cache for {CacheKey}")]
    private static partial void LogDetectionCacheWriteError(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error deleting detection cache for {CacheKey}")]
    private static partial void LogDetectionCacheDeleteError(ILogger logger, Exception ex, string cacheKey);
}
