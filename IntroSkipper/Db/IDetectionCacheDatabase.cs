// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Cohesive facade over the detection cache database (<c>introskipper-cache.db</c>).
/// Owns every read and write against <see cref="DetectionCacheDbContext"/> as well as the
/// schema lifecycle (<c>EnsureCreated</c> with delete-and-recreate corruption recovery).
/// The synchronous members mirror the synchronous call patterns of the analysis pipeline.
/// Initialization failures make the cache temporarily unavailable; operations return neutral
/// results and retry initialization on the next call. Delete operations are best-effort —
/// the cache is an optimization, so database errors are logged and swallowed (returning 0)
/// instead of propagating to callers.
/// </summary>
public interface IDetectionCacheDatabase
{
    /// <summary>
    /// Ensures the cache schema exists, recreating corrupt cache files when possible.
    /// Concurrent callers share one attempt; a failed attempt is logged and reset so the
    /// next call retries.
    /// </summary>
    /// <returns><see langword="true"/> when the cache is ready.</returns>
    bool TryInitialize();

    /// <summary>
    /// Returns the cache entry matching the exact key, or <see langword="null"/>.
    /// Start/End are compared with equality, which is safe only because the exact same
    /// double values that were written are used for lookup.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start of the analyzed range.</param>
    /// <param name="end">End of the analyzed range.</param>
    /// <returns>The matching entry, or <see langword="null"/>.</returns>
    DbDetectionCache? FindEntry(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end);

    /// <summary>
    /// Inserts or updates the cache entry for the given key.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start of the analyzed range.</param>
    /// <param name="end">End of the analyzed range.</param>
    /// <param name="data">Compressed detection data.</param>
    /// <param name="configHash">Configuration hash that produced the data.</param>
    void Upsert(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, byte[] data, string configHash);

    /// <summary>
    /// Returns whether a cache entry exists for the given key whose configuration hash is
    /// either empty (legacy entry) or equal to <paramref name="expectedConfigHash"/>.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start of the analyzed range.</param>
    /// <param name="end">End of the analyzed range.</param>
    /// <param name="expectedConfigHash">Expected configuration hash.</param>
    /// <returns><see langword="true"/> when a usable entry exists.</returns>
    bool HasEntry(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, string expectedConfigHash);

    /// <summary>
    /// Deletes all cache entries for an item. Best-effort: database errors are logged
    /// and swallowed.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
    int DeleteForItem(Guid itemId);

    /// <summary>
    /// Deletes all cache entries for an analysis mode. Best-effort: database errors are
    /// logged and swallowed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
    int DeleteByMode(AnalysisMode mode);

    /// <summary>
    /// Returns the distinct item IDs present in the cache that are not part of
    /// <paramref name="validItemIds"/>. The valid set is bound as a single JSON
    /// parameter (<c>json_each</c>), so the query is safe for arbitrarily large libraries.
    /// </summary>
    /// <param name="validItemIds">Item IDs that are still valid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stale item IDs.</returns>
    Task<IReadOnlyCollection<Guid>> GetStaleItemIdsAsync(IReadOnlySet<Guid> validItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cache entries for the given items in a single statement; the ID set
    /// is bound as one JSON parameter, so the item count is unbounded. Best-effort:
    /// database errors are logged and swallowed (cancellation still propagates).
    /// </summary>
    /// <param name="itemIds">Item IDs whose cache entries should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
    Task<int> DeleteForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
}
