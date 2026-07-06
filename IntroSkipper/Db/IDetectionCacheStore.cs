// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Persistence operations for the FFmpeg detection cache (<see cref="DbDetectionCache"/>, stored in
/// <c>introskipper-cache.db</c>). Pure storage: compression, configuration hashing and cache-miss
/// policy live in <see cref="IntroSkipper.FFmpeg.DetectionCacheService"/>. The synchronous members
/// mirror the synchronous <see cref="IntroSkipper.FFmpeg.IDetectionCacheService"/> surface.
/// </summary>
public interface IDetectionCacheStore
{
    /// <summary>
    /// Finds the cache entry with the exact key (item, mode, type, start, end), or <see langword="null"/>.
    /// Start/End are compared with equality, which is safe only because lookups reuse the exact
    /// double values that were written.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start time in seconds.</param>
    /// <param name="end">End time in seconds.</param>
    /// <returns>The matching entry, or <see langword="null"/>.</returns>
    DbDetectionCache? Find(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end);

    /// <summary>
    /// Returns whether a cache entry with the exact key exists and its stored configuration hash is
    /// either empty or equal to <paramref name="expectedConfigHash"/>.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start time in seconds.</param>
    /// <param name="end">End time in seconds.</param>
    /// <param name="expectedConfigHash">Configuration hash the entry must match (empty stored hashes always match).</param>
    /// <returns><see langword="true"/> when a valid entry exists.</returns>
    bool Exists(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, string expectedConfigHash);

    /// <summary>
    /// Inserts or updates the cache entry with the exact key.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start time in seconds.</param>
    /// <param name="end">End time in seconds.</param>
    /// <param name="data">Compressed payload.</param>
    /// <param name="configHash">Configuration hash that produced the payload.</param>
    void Upsert(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, byte[] data, string configHash);

    /// <summary>
    /// Deletes all cache entries for an item.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    void DeleteForItem(Guid itemId);

    /// <summary>
    /// Deletes all cache entries for an analysis mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    void DeleteByMode(AnalysisMode mode);

    /// <summary>
    /// Gets the distinct item IDs present in the cache.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Distinct item IDs.</returns>
    Task<IReadOnlyList<Guid>> GetItemIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cache entries belonging to the supplied items. Safe for arbitrarily large ID
    /// sets: the collection is sent as a single JSON parameter.
    /// </summary>
    /// <param name="itemIds">Item IDs whose entries should be deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows.</returns>
    Task<int> DeleteForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
}
