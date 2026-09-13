// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Erases items' stored segments and analysis state, optionally their detection cache
/// rows, and converges their Jellyfin mirrors. Shared by the dashboard's erase endpoints
/// and the manual scan.
/// </summary>
public interface ISegmentEraser
{
    /// <summary>
    /// Erases the items. The erase journals every affected item's projection, so the
    /// mirrors converge durably even when the immediate projection cannot finish.
    /// </summary>
    /// <param name="itemIds">The items to erase.</param>
    /// <param name="eraseCache">Whether to delete the items' detection cache rows too.</param>
    /// <param name="cancellationToken">Cancellation token. The cache cleanup ignores it: the main database is already consistent by then.</param>
    /// <returns>The number of removed segment rows and cache rows.</returns>
    Task<(int RemovedSegments, int RemovedCacheEntries)> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, bool eraseCache, CancellationToken cancellationToken);
}
