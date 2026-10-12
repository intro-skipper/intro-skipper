// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Erases stored segments and analysis state, optionally the matching detection cache
/// rows and user-created tombstones, and converges the affected items' Jellyfin mirrors.
/// The one path for bulk erases: the dashboard's erase endpoints, the manual scan, the
/// mode erase and the cache cleanup task.
/// </summary>
/// <remarks>
/// Every erase journals the affected items' projections in the same transaction as the
/// delete, so the mirrors converge durably even when the immediate projection cannot
/// finish: cancellation or a Jellyfin failure leaves the work to the projection worker.
/// Only the erased items are converged right away; other items' pending work keeps its
/// backoff. The cache cleanup is best effort and ignores cancellation, because the main
/// database is already consistent by then.
/// </remarks>
public interface ISegmentEraser
{
    /// <summary>
    /// Erases the items' segments and analysis state.
    /// </summary>
    /// <param name="itemIds">The items to erase.</param>
    /// <param name="eraseCache"><see langword="true"/> to delete the items' detection cache rows too; otherwise, <see langword="false"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="eraseTombstones"><see langword="true"/> to delete user-created timestamp locks; otherwise, preserve them.</param>
    /// <returns>The number of removed segment rows and cache rows.</returns>
    Task<(int RemovedSegments, int RemovedCacheEntries)> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, bool eraseCache, bool eraseTombstones = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erases every stored segment of a mode and the mode's analysis records, so the
    /// next scan re-detects it. User-created timestamp locks are deleted by default
    /// and can be preserved by the caller.
    /// </summary>
    /// <param name="mode">One of the enumeration values that specifies the mode to erase.</param>
    /// <param name="eraseCache"><see langword="true"/> to delete the mode's detection cache rows too, which only Introduction and Credits erase; otherwise, <see langword="false"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="eraseTombstones"><see langword="true"/> to delete user-created timestamp locks; otherwise, preserve them.</param>
    /// <returns>A task that completes when the erase is committed and the immediate convergence attempt is over.</returns>
    Task EraseModeAsync(AnalysisMode mode, bool eraseCache, bool eraseTombstones = true, CancellationToken cancellationToken = default);
}
