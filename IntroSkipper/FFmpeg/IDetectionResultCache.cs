// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides hot-path detection-cache read/write operations for FFmpeg media detection.
/// </summary>
public interface IDetectionResultCache
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
}
