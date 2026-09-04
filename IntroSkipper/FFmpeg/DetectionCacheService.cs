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
public sealed partial class DetectionCacheService(ILogger<DetectionCacheService> logger, IDetectionCacheDatabase cacheDatabase)
{
    private readonly ILogger<DetectionCacheService> _logger = logger;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;

    private static bool IsEnabled => Plugin.Instance?.Configuration.CacheFingerprints ?? false;

    /// <summary>
    /// Tries to read a cached detection result from the SQLite DB.
    /// </summary>
    /// <typeparam name="T">The element type of the cached result array.</typeparam>
    /// <param name="itemId">The media item ID.</param>
    /// <param name="mode">One of the enumeration values that specifies the analysis mode.</param>
    /// <param name="type">One of the enumeration values that specifies the cache entry type.</param>
    /// <param name="start">The start position used as a cache key component.</param>
    /// <param name="end">The end position used as a cache key component.</param>
    /// <param name="result">When this method returns, contains the cached result array, or an empty array if the cache was missed. This parameter is treated as uninitialized.</param>
    /// <param name="cacheVariant">Optional effective stream identity for stream-sensitive cache entries.</param>
    /// <returns><see langword="true"/> if a valid cache entry was found; otherwise, <see langword="false"/>.</returns>
    public bool TryRead<T>(
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        out T[] result,
        string? cacheVariant = null)
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

            var expectedHash = ConfigHasher.DetectionCache(Plugin.Instance?.Configuration ?? new(), type, mode, cacheVariant);
            if (!string.IsNullOrEmpty(entry.ConfigHash)
                && !string.Equals(entry.ConfigHash, expectedHash, StringComparison.Ordinal))
            {
                return false;
            }

            result = DecompressBrotli<T[]>(entry.Data) ?? [];
            LogDetectionCacheHit(_logger, itemId, mode, type);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidDataException or DbException)
        {
            LogDetectionCacheReadError(_logger, ex, itemId, mode, type);
            return false;
        }
    }

    /// <summary>
    /// Writes a detection result to the SQLite cache. A failed write is logged and swallowed:
    /// the cache is an optimization and must never discard a valid analysis result.
    /// </summary>
    /// <typeparam name="T">The element type of the result array to cache.</typeparam>
    /// <param name="itemId">The media item ID.</param>
    /// <param name="mode">One of the enumeration values that specifies the analysis mode.</param>
    /// <param name="type">One of the enumeration values that specifies the cache entry type.</param>
    /// <param name="start">The start position used as a cache key component.</param>
    /// <param name="end">The end position used as a cache key component.</param>
    /// <param name="items">The result array to cache.</param>
    /// <param name="cacheVariant">Optional effective stream identity for stream-sensitive cache entries.</param>
    public void Write<T>(
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        T[] items,
        string? cacheVariant = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var data = CompressBrotli(items);
        var configHash = ConfigHasher.DetectionCache(Plugin.Instance?.Configuration ?? new(), type, mode, cacheVariant);

        try
        {
            _cacheDatabase.Upsert(itemId, mode, type, start, end, data, configHash);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogDetectionCacheWriteError(_logger, ex, itemId, mode, type);
        }
    }

    /// <summary>
    /// Checks if a fingerprint cache entry exists for the episode.
    /// </summary>
    /// <param name="episode">The queued episode to check.</param>
    /// <param name="mode">One of the enumeration values that specifies the analysis mode.</param>
    /// <remarks>Stream-scoped entries are considered present here; the fingerprint read validates the exact stream and configuration before reuse.</remarks>
    /// <returns><see langword="true"/> if a fingerprint cache entry exists; otherwise, <see langword="false"/>.</returns>
    public bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var (start, end) = episode.GetFingerprintRange(mode);

        try
        {
            var entry = _cacheDatabase.FindEntry(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end);
            if (entry is null)
            {
                return false;
            }

            var expectedHash = ConfigHasher.DetectionCache(Plugin.Instance?.Configuration ?? new(), CacheEntryType.Chromaprint, mode);

            // Stream-scoped rows are accepted optimistically: whether the effective stream still
            // matches is only decided at read time, and a mismatch there just refingerprints
            // the episode.
            return string.IsNullOrEmpty(entry.ConfigHash)
                || string.Equals(entry.ConfigHash, expectedHash, StringComparison.Ordinal)
                || ConfigHasher.IsStreamScopedDetectionCacheHash(entry.ConfigHash);
        }
        catch (DbException ex)
        {
            LogDetectionCacheReadError(_logger, ex, episode.EpisodeId, mode, CacheEntryType.Chromaprint);
        }

        return false;
    }

    /// <summary>
    /// Deletes cache rows whose configuration hash no read path can accept under the current
    /// plugin configuration: superseded hash inputs and hashes of settings values that have
    /// since changed. Rows with an empty hash and stream-scoped rows are kept, mirroring the optimistic
    /// acceptance of the read paths. A row deleted here would be refingerprinted anyway;
    /// the cost of a false delete is one recomputation, never lost analysis results.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
    public async Task<int> DeleteUnreadableEntriesAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new();

        // Every hash some read path accepts under the current configuration: the current
        // hash of each (type, mode) pair. Inputs that ignore type or mode collapse in the set.
        var acceptedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            foreach (var type in Enum.GetValues<CacheEntryType>())
            {
                acceptedHashes.Add(ConfigHasher.DetectionCache(config, type, mode));
            }
        }

        return await _cacheDatabase
            .DeleteEntriesWithUnknownConfigHashAsync(acceptedHashes, ConfigHasher.StreamScopedDetectionCacheHashPrefix, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes and compresses a value using Brotli compression.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize and compress.</param>
    /// <returns>The Brotli-compressed data.</returns>
    internal static byte[] CompressBrotli<T>(T value)
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
    internal static T? DecompressBrotli<T>(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(brotli);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detection cache hit for {ItemId} {Mode} {Type}")]
    private static partial void LogDetectionCacheHit(ILogger logger, Guid itemId, AnalysisMode mode, CacheEntryType type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading detection cache for {ItemId} {Mode} {Type}")]
    private static partial void LogDetectionCacheReadError(ILogger logger, Exception ex, Guid itemId, AnalysisMode mode, CacheEntryType type);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error writing detection cache for {ItemId} {Mode} {Type}")]
    private static partial void LogDetectionCacheWriteError(ILogger logger, Exception ex, Guid itemId, AnalysisMode mode, CacheEntryType type);
}
