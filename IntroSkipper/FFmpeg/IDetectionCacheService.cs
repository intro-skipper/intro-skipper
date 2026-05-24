// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides cache management operations beyond hot-path detection-result read/write.
/// </summary>
public interface IDetectionCacheService : IDetectionResultCache
{
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
    /// The method is idempotent; after a successful pass with no supported legacy candidates
    /// remaining, later calls are no-ops. Failed or disabled passes may be retried.
    /// </summary>
    /// <param name="episodes">Episodes eligible for this migration pass, used to resolve fingerprint ranges for chromaprint migration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task MigrateLegacyCachesAsync(IEnumerable<QueuedEpisode> episodes, CancellationToken cancellationToken = default);
}
