// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
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

        // The facade logs and swallows cache database errors.
        var removedCacheEntries = eraseCache
            ? await _cacheDatabase.DeleteForItemsAsync(itemIds, CancellationToken.None).ConfigureAwait(false)
            : 0;

        await _segmentChange.ProjectItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);
        return (removedSegments, removedCacheEntries);
    }

    /// <inheritdoc />
    public async Task EraseModeAsync(AnalysisMode mode, bool eraseCache, CancellationToken cancellationToken)
    {
        var itemIds = await _database.DeleteSegmentsByModeAsync(mode, cancellationToken).ConfigureAwait(false);

        if (eraseCache && mode is AnalysisMode.Introduction or AnalysisMode.Credits)
        {
            await _cacheDatabase.DeleteByModeAsync(mode, CancellationToken.None).ConfigureAwait(false);
        }

        await _segmentChange.ProjectItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);
    }
}
