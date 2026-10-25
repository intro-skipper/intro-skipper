// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Erases automatic segments and analysis state, optionally the matching detection cache
/// rows and tombstones, and converges the affected items' Jellyfin mirrors. The one path
/// for bulk erases: the dashboard's erase endpoints, the mode erase and the manual scan.
/// </summary>
/// <remarks>
/// Every erase keeps user segments, because analysis did not create them. They settle
/// their modes, so the next pass analyzes only the other modes. Every erase journals the
/// affected items' projections in the same transaction as the delete, so the mirrors
/// converge durably even when the immediate projection cannot finish: cancellation or a
/// Jellyfin failure leaves the work to the projection worker.
/// Only the erased items are converged right away; other items' pending work keeps its
/// backoff. The cache cleanup is best effort and ignores cancellation, because the main
/// database is already consistent by then.
/// </remarks>
public interface ISegmentEraser
{
    /// <summary>
    /// Erases the items' automatic segments and analysis records.
    /// </summary>
    /// <remarks>
    /// The id count is unbounded: each delete binds the ids as one parameter.
    /// </remarks>
    /// <param name="itemIds">The items to erase.</param>
    /// <param name="eraseCache"><see langword="true"/> to delete the items' detection cache rows too; otherwise, <see langword="false"/>.</param>
    /// <param name="eraseTombstones"><see langword="true"/> to delete the items' tombstones too, so detection may add the deleted ranges again; otherwise, <see langword="false"/>, and the tombstones keep refusing re-detections.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of removed segment rows (automatic segments, plus tombstones when <paramref name="eraseTombstones"/> is set) and the number of removed cache rows, 0 when the cache delete failed.</returns>
    Task<(int RemovedSegments, int RemovedCacheEntries)> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, bool eraseCache, bool eraseTombstones, CancellationToken cancellationToken);

    /// <summary>
    /// Erases a mode's automatic segments and analysis records, so the next scan
    /// re-detects the mode on every item that holds no user segment of the mode.
    /// </summary>
    /// <param name="mode">One of the enumeration values that specifies the mode to erase.</param>
    /// <param name="eraseCache"><see langword="true"/> to delete the mode's detection cache rows too, which only Introduction and Credits erase; otherwise, <see langword="false"/>.</param>
    /// <param name="eraseTombstones"><see langword="true"/> to delete the mode's tombstones too, so detection may add the deleted ranges again; otherwise, <see langword="false"/>, and the tombstones keep refusing re-detections.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the erase is committed and the immediate convergence attempt is over.</returns>
    Task EraseModeAsync(AnalysisMode mode, bool eraseCache, bool eraseTombstones, CancellationToken cancellationToken);
}
