// SPDX-FileCopyrightText: 2024-2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Manages reading and writing detection results to/from the SQLite cache.
/// </summary>
public interface IDetectionCacheService
{
    /// <summary>Gets a value indicating whether caching is enabled in plugin config.</summary>
    /// <value><see langword="true"/> if caching is enabled; otherwise, <see langword="false"/>.</value>
    bool IsEnabled { get; }

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
    /// <param name="legacyConfigHash">Optional legacy hash that is safe to accept for this effective stream.</param>
    /// <returns><see langword="true"/> if a valid cache entry was found; otherwise, <see langword="false"/>.</returns>
    bool TryRead<T>(
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        out T[] result,
        string? cacheVariant = null,
        string? legacyConfigHash = null);

    /// <summary>
    /// Writes a detection result to the SQLite cache.
    /// </summary>
    /// <typeparam name="T">The element type of the result array to cache.</typeparam>
    /// <param name="itemId">The media item ID.</param>
    /// <param name="mode">One of the enumeration values that specifies the analysis mode.</param>
    /// <param name="type">One of the enumeration values that specifies the cache entry type.</param>
    /// <param name="start">The start position used as a cache key component.</param>
    /// <param name="end">The end position used as a cache key component.</param>
    /// <param name="items">The result array to cache.</param>
    /// <param name="cacheVariant">Optional effective stream identity for stream-sensitive cache entries.</param>
    /// <returns><see langword="true"/> if the write succeeded; otherwise, <see langword="false"/>.</returns>
    bool Write<T>(
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        T[] items,
        string? cacheVariant = null);

    /// <summary>
    /// Checks if a fingerprint cache entry exists for the episode.
    /// </summary>
    /// <param name="episode">The queued episode to check.</param>
    /// <param name="mode">One of the enumeration values that specifies the analysis mode.</param>
    /// <remarks>Stream-scoped entries are considered present here; the fingerprint read validates the exact stream and configuration before reuse.</remarks>
    /// <returns><see langword="true"/> if a fingerprint cache entry exists; otherwise, <see langword="false"/>.</returns>
    bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode);
}
