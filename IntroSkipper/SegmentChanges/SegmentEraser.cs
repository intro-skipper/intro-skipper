// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;

namespace IntroSkipper.SegmentChanges;

/// <inheritdoc />
internal sealed class SegmentEraser(IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, SegmentChange segmentChange) : ISegmentEraser
{
    private readonly IIntroSkipperDatabase _database = database;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly SegmentChange _segmentChange = segmentChange;

    /// <inheritdoc />
    public async Task<(int RemovedSegments, int RemovedCacheEntries)> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, bool eraseCache, CancellationToken cancellationToken)
    {
        var removedSegments = await _database.EraseItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);

        // Best-effort cache cleanup (the facade logs and swallows database errors),
        // not bound to request cancellation: the main database is already consistent.
        var removedCacheEntries = eraseCache
            ? await _cacheDatabase.DeleteForItemsAsync(itemIds, CancellationToken.None).ConfigureAwait(false)
            : 0;

        // The erase journaled every affected item's projection; converge exactly
        // those items now, unrelated pending work keeps its backoff. Anything
        // this pass cannot finish stays journaled and the worker completes it.
        await _segmentChange.ProjectItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);
        return (removedSegments, removedCacheEntries);
    }
}
