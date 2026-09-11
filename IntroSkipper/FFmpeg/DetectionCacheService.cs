// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
    /// <param name="legacyConfigHash">Pre-stream-selection hash to accept as well, when the caller knows the row's stream is still the effective one.</param>
    /// <returns><see langword="true"/> if a valid cache entry was found; otherwise, <see langword="false"/>.</returns>
    public bool TryRead<T>(
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        out T[] result,
        string? cacheVariant = null,
        string? legacyConfigHash = null)
    {
        result = [];

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
            var hashMatches = string.IsNullOrEmpty(entry.ConfigHash)
                || string.Equals(entry.ConfigHash, expectedHash, StringComparison.Ordinal)
                || string.Equals(entry.ConfigHash, legacyConfigHash, StringComparison.Ordinal);

            // Chromaprint rows contain raw fingerprint points.  Older releases also put
            // processing settings in their hash, so a changed comparison setting must not
            // discard an otherwise valid fingerprint. Stream-scoped rows are accepted only
            // when their effective stream matches; unscoped rows must match the explicitly
            // recognized legacy hash supplied by the fingerprint caller.
            if (!hashMatches && type == CacheEntryType.Chromaprint)
            {
                hashMatches = ConfigHasher.IsStreamScopedDetectionCacheHashFor(entry.ConfigHash, cacheVariant);
            }

            if (!hashMatches)
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
    /// Checks if a fingerprint cache entry exists for the episode. A mode that shares another
    /// mode's row (see <see cref="QueuedEpisode.FingerprintCacheMode"/>) also counts a row an
    /// older release wrote under its own key, so episodes fingerprinted before the rows were
    /// shared keep their place in the comparison pool.
    /// </summary>
    /// <param name="episode">The queued episode to check.</param>
    /// <param name="mode">One of the enumeration values that specifies the analysis mode.</param>
    /// <remarks>Rows are considered present here; the fingerprint read validates the effective stream before reuse.</remarks>
    /// <returns><see langword="true"/> if a fingerprint cache entry exists; otherwise, <see langword="false"/>.</returns>
    public bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        var cacheMode = QueuedEpisode.FingerprintCacheMode(mode);
        var (start, end) = episode.GetFingerprintRange(cacheMode);

        return HasReadableFingerprintRow(episode.EpisodeId, cacheMode, start, end)
            || (cacheMode != mode && HasReadableFingerprintRow(episode.EpisodeId, mode, start, end));
    }

    private bool HasReadableFingerprintRow(Guid itemId, AnalysisMode rowMode, double start, double end)
    {
        try
        {
            var entry = _cacheDatabase.FindEntry(itemId, rowMode, CacheEntryType.Chromaprint, start, end);
            if (entry is null)
            {
                return false;
            }

            // Whether the effective stream still matches is decided by FingerprintAsync,
            // which probes the media before reading. A row is therefore enough to keep an
            // analyzed episode in the comparison pool; a stream mismatch merely causes one
            // fresh fingerprint instead of silently dropping the episode from analysis.
            return true;
        }
        catch (DbException ex)
        {
            LogDetectionCacheReadError(_logger, ex, itemId, rowMode, CacheEntryType.Chromaprint);
        }

        return false;
    }

    /// <summary>
    /// Deletes settings-sensitive detection rows whose configuration hash no read path can
    /// accept under the current plugin configuration. Chromaprint rows are retained because
    /// their points are independent of those processing settings and their stream/range are
    /// validated by the fingerprint read path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
    public async Task<int> DeleteUnreadableEntriesAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new();

        // Every hash some read path accepts under the current configuration: the current
        // hash of each (type, mode) pair plus the legacy pre-stream-selection Chromaprint
        // hash. Inputs that ignore type or mode collapse in the set.
        var acceptedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            foreach (var type in Enum.GetValues<CacheEntryType>())
            {
                acceptedHashes.Add(ConfigHasher.DetectionCache(config, type, mode));
            }

            acceptedHashes.Add(ConfigHasher.LegacyChromaprintCacheWithoutLanguage(config, mode));
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
