// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides detection-cache operations: SQLite read/write, Brotli compression,
/// legacy on-disk cache migration, and cache management (delete by item or mode).
/// </summary>
public interface IDetectionCacheService
{
    /// <summary>
    /// Tries to read a cached detection result from the SQLite cache asynchronously.
    /// </summary>
    /// <typeparam name="T">Element type of the cached array.</typeparam>
    /// <param name="key">Cache entry key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached array on success; otherwise <c>null</c>.</returns>
    Task<T[]?> TryReadJsonCacheAsync<T>(DetectionCacheKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a detection result to the SQLite cache with Brotli compression asynchronously.
    /// </summary>
    /// <typeparam name="T">Element type of the array to cache.</typeparam>
    /// <param name="key">Cache entry key.</param>
    /// <param name="items">Data to cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the write succeeded; <c>false</c> on database error (non-fatal).</returns>
    Task<bool> WriteJsonCacheAsync<T>(DetectionCacheKey key, T[] items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries for a media item from the SQLite cache and legacy on-disk files.
    /// </summary>
    /// <param name="id">Media item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task DeleteFingerprintCacheAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries for a specific analysis mode from the SQLite cache and legacy on-disk files.
    /// </summary>
    /// <param name="mode">Analysis mode whose cache entries to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task DeleteCacheFilesAsync(AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> if a fingerprint cache entry exists in the SQLite cache.
    /// </summary>
    /// <param name="episode">Episode to check.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if any fingerprint cache entry exists; otherwise <c>false</c>.</returns>
    Task<bool> HasCachedFingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes SQLite cache rows and legacy on-disk cache files for items outside the provided enabled set.
    /// </summary>
    /// <param name="enabledItemIds">Enabled media item identifiers to keep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task DeleteStaleCachesAsync(IReadOnlySet<Guid> enabledItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch-migrates all legacy on-disk cache files into the SQLite database.
    /// This method is idempotent and only performs migration work on the first call.
    /// </summary>
    /// <param name="episodes">All queued episodes, used to resolve fingerprint ranges for chromaprint migration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task MigrateLegacyCachesAsync(IEnumerable<QueuedEpisode> episodes, CancellationToken cancellationToken = default);
}
