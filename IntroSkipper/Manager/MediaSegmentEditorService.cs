using IntroSkipper.Data;
using IntroSkipper.Db;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Manager;

/// <summary>
/// Coordinates editor operations across Intro Skipper's segment database and Jellyfin's
/// media-segment store.
/// </summary>
/// <remarks>
/// Operations that mutate an item wait asynchronously for its process-wide lock, so they
/// serialize with refreshes and other editor mutations without blocking a thread. The two
/// backing stores cannot share a transaction; mutating operations compensate the plugin
/// database when the subsequent Jellyfin write fails.
/// </remarks>
/// <param name="segmentStore">The direct store for Jellyfin's media segments.</param>
/// <param name="database">The segment database facade.</param>
/// <param name="segmentProviders">The registered media segment providers used to resolve display names.</param>
public class MediaSegmentEditorService(
    IJellyfinSegmentStore segmentStore,
    IIntroSkipperDatabase database,
    IEnumerable<IMediaSegmentProvider> segmentProviders)
{
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly IEnumerable<IMediaSegmentProvider> _segmentProviders = segmentProviders;

    /// <summary>
    /// Creates or replaces a Jellyfin media segment for an item.
    /// </summary>
    /// <remarks>
    /// The operation waits asynchronously for the item's mutation lock. Non-commercial
    /// segments replace every existing segment of that type, regardless of provider, in
    /// Jellyfin's transaction. Commercial segments are added only when no entry with the
    /// same start and end ticks exists.
    /// </remarks>
    /// <param name="item">The media item that owns the segment.</param>
    /// <param name="segment">The segment to persist in Jellyfin's database.</param>
    /// <param name="cancellationToken">The token that cancels waiting or the store operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled while waiting or writing.</exception>
    public async Task CreateOrReplaceSegmentAsync(BaseItem item, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(segment);

        var itemLock = MediaSegmentItemLock.Get(item.Id);
        await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (segment.Type == MediaSegmentType.Commercial)
            {
                await _segmentStore.CreateCommercialIfAbsentAsync(item.Id, segment, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _segmentStore.ReplaceTypeAsync(item.Id, segment, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            itemLock.Release();
        }
    }

    /// <summary>
    /// Replaces every editor-managed segment of the given modes for an item.
    /// </summary>
    /// <remarks>
    /// The operation waits asynchronously for the item's mutation lock. It replaces plugin
    /// rows first, then replaces Jellyfin rows of the mapped types regardless of provider.
    /// If the Jellyfin write fails, it restores the exact prior plugin rows and rethrows.
    /// After both writes commit, it removes modes that became empty from the season's
    /// analyzed-state list using an uncancelable cleanup operation.
    /// </remarks>
    /// <param name="item">The media item whose segments are replaced.</param>
    /// <param name="seasonStateKey">The key under which the item's analyzed-state lists are stored.</param>
    /// <param name="segments">The segments that will exist for <paramref name="targetModes"/> after the operation.</param>
    /// <param name="targetModes">The analysis modes whose segments are authoritatively replaced.</param>
    /// <param name="cancellationToken">The token that cancels waiting or work before both stores commit.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/>, <paramref name="segments"/>, or <paramref name="targetModes"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before both stores commit.</exception>
    public async Task ReplaceEditorSegmentsAsync(
        BaseItem item,
        Guid seasonStateKey,
        IReadOnlyList<MediaSegmentDto> segments,
        IReadOnlyCollection<AnalysisMode> targetModes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(targetModes);

        var newRows = segments
            .Select(segment => new DbSegment(
                new Segment(item.Id, new TimeRange(
                    TimeSpan.FromTicks(segment.StartTicks).TotalSeconds,
                    TimeSpan.FromTicks(segment.EndTicks).TotalSeconds)),
                AnalysisHelpers.MapSegmentTypeToMode(segment.Type),
                isUserProvided: true))
            .ToList();
        var mappedTypes = targetModes.Select(mode => AnalysisHelpers.ModeToSegmentType[mode]).ToList();

        var itemLock = MediaSegmentItemLock.Get(item.Id);
        await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var priorRows = await _database
                .ReplaceItemSegmentsAsync(item.Id, targetModes, newRows, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await _segmentStore.ReplaceEditableTypesAsync(item.Id, segments, mappedTypes, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Restore the exact prior plugin rows after a Jellyfin failure. The
                // compensation is uncancelable because the plugin replacement committed.
                await _database
                    .ReplaceItemSegmentsAsync(item.Id, targetModes, priorRows, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            // Once both stores commit, clear modes that became empty without allowing
            // request cancellation to leave the season state stale.
            foreach (var mode in targetModes)
            {
                var hadRows = priorRows.Any(row => row.Type == mode);
                var hasRows = newRows.Any(row => row.Type == mode);
                if (hadRows && !hasRows)
                {
                    await _database.RemoveEpisodeIdAsync(seasonStateKey, mode, item.Id, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            itemLock.Release();
        }
    }

    /// <summary>
    /// Returns an item's Jellyfin segments annotated with Intro Skipper metadata.
    /// </summary>
    /// <remarks>
    /// The Jellyfin and plugin-database reads are independent and are not transactional,
    /// so concurrent external writes can produce a temporarily mixed view.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose segments are retrieved.</param>
    /// <param name="cancellationToken">The token that cancels the asynchronous reads.</param>
    /// <returns>An annotated segment list ordered by start position.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled while reading.</exception>
    public async Task<IReadOnlyList<EditorSegmentDto>> GetEditorSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // Independent reads against two different SQLite files, neither transactional.
        var snapshotsTask = _segmentStore.GetItemSegmentsAsync(itemId, cancellationToken);
        var pluginRowsTask = _database.GetSegmentsAsync(itemId, cancellationToken);
        await Task.WhenAll(snapshotsTask, pluginRowsTask).ConfigureAwait(false);
        var snapshots = await snapshotsTask.ConfigureAwait(false);
        var pluginRows = await pluginRowsTask.ConfigureAwait(false);

        var providerNamesById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in _segmentProviders)
        {
            providerNamesById.TryAdd(JellyfinSegmentStore.DeriveProviderId(provider.Name), provider.Name);
        }

        var result = new List<EditorSegmentDto>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            providerNamesById.TryGetValue(snapshot.ProviderId, out var providerName);
            result.Add(new EditorSegmentDto(
                snapshot.Id,
                snapshot.ItemId,
                snapshot.Type,
                snapshot.StartTicks,
                snapshot.EndTicks,
                snapshot.ProviderId,
                providerName,
                ResolveIsUserProvided(snapshot, pluginRows)));
        }

        return result;
    }

    /// <summary>
    /// Lists Jellyfin media segment rows whose items no longer exist, grouped per item and
    /// split by owning provider. Includes rows keyed by the empty guid.
    /// </summary>
    /// <param name="itemExists">Predicate deciding whether an item id is a live library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The orphaned items.</returns>
    public async Task<IReadOnlyList<OrphanedItemSegments>> GetOrphanedSegmentsAsync(Func<Guid, bool> itemExists, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemExists);

        var counts = await _segmentStore.GetItemSegmentCountsAsync(cancellationToken).ConfigureAwait(false);

        return counts
            .Where(entry => !itemExists(entry.ItemId))
            .Select(entry => new OrphanedItemSegments(entry.ItemId, entry.OwnCount, entry.OtherCount))
            .ToList();
    }

    /// <summary>
    /// Deletes Intro Skipper's Jellyfin segment rows for items that no longer exist. The
    /// orphan set is always recomputed because a caller-supplied list is time-of-check
    /// sensitive: a library scan can restore items between listing and deleting. Other
    /// providers' rows and empty-guid rows are retained.
    /// </summary>
    /// <param name="itemExists">Predicate deciding whether an item id is a live library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of orphaned items whose rows were deleted.</returns>
    public async Task<int> DeleteOrphanedSegmentsAsync(Func<Guid, bool> itemExists, CancellationToken cancellationToken)
    {
        var orphans = await GetOrphanedSegmentsAsync(itemExists, cancellationToken).ConfigureAwait(false);
        var deletableIds = orphans
            .Where(orphan => orphan.OwnCount > 0 && orphan.ItemId != Guid.Empty)
            .Select(orphan => orphan.ItemId)
            .ToList();

        await _segmentStore.DeleteOwnSegmentsAsync(deletableIds, cancellationToken).ConfigureAwait(false);

        return deletableIds.Count;
    }

    /// <summary>
    /// Deletes one editor segment from both stores.
    /// </summary>
    /// <remarks>
    /// The operation waits asynchronously for the item's mutation lock. It removes the
    /// plugin row before deleting the Jellyfin row. If the Jellyfin delete fails, it
    /// restores the removed plugin rows without honoring cancellation. Season-state
    /// bookkeeping is intentionally the caller's responsibility after a successful delete.
    /// </remarks>
    /// <param name="itemId">The ID of the item that owns the segment.</param>
    /// <param name="segmentId">The ID of the Jellyfin segment to delete.</param>
    /// <param name="mode">One of the analysis modes that specifies the expected segment type.</param>
    /// <param name="cancellationToken">The token that cancels waiting or work before the Jellyfin delete.</param>
    /// <returns>
    /// A result that reports whether deletion occurred and, when it did not, the actual
    /// mismatched type or <see langword="null"/> for a missing commercial segment.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before the Jellyfin delete.</exception>
    public async Task<(bool Deleted, MediaSegmentType? ActualType)> DeleteSegmentAsync(
        Guid itemId,
        Guid segmentId,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var itemLock = MediaSegmentItemLock.Get(itemId);
        await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingSegment = await _segmentStore.GetSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
            if (existingSegment is not null
                && existingSegment.Type != AnalysisHelpers.ModeToSegmentType[mode])
            {
                return (false, existingSegment.Type);
            }

            Segment? dbSegment = null;
            if (existingSegment is not null)
            {
                var startSeconds = TimeSpan.FromTicks(existingSegment.StartTicks).TotalSeconds;
                var endSeconds = TimeSpan.FromTicks(existingSegment.EndTicks).TotalSeconds;
                dbSegment = new Segment(itemId, new TimeRange(startSeconds, endSeconds));
            }

            if (dbSegment is null && mode == AnalysisMode.Commercial)
            {
                return (false, null);
            }

            // Non-commercial modes have exactly one row per item and mode, so delete that
            // unambiguous counterpart even if Jellyfin's reported range has drifted.
            var deleteSegment = mode == AnalysisMode.Commercial ? dbSegment : null;
            var deletedRows = await _database
                .DeleteTimestampAsync(itemId, mode, deleteSegment, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                // Deliberately uncancellable: the plugin row is already gone, so a token
                // canceled mid-delete could tear the two stores apart. Scoped to the item
                // so a stale segment id can never remove another item's segment.
                await _segmentStore.DeleteSegmentAsync(itemId, segmentId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Once the plugin delete commits, compensation must finish even if the
                // request is canceled.
                if (deletedRows.Count > 0)
                {
                    foreach (var row in deletedRows)
                    {
                        await _database
                            .UpdateTimestampAsync(
                                row.ToSegment(),
                                mode,
                                isUserProvided: row.IsUserProvided,
                                configHash: row.ConfigHash,
                                cancellationToken: CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                else if (dbSegment is not null)
                {
                    await _database
                        .UpdateTimestampAsync(dbSegment, mode, cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }

                throw;
            }

            return (true, null);
        }
        finally
        {
            itemLock.Release();
        }
    }

    private static bool? ResolveIsUserProvided(JellyfinSegmentSnapshot snapshot, IReadOnlyList<DbSegment> pluginRows)
    {
        if (!string.Equals(snapshot.ProviderId, JellyfinSegmentStore.ProviderId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!AnalysisHelpers.TryMapSegmentTypeToMode(snapshot.Type, out var mode))
        {
            return null;
        }

        if (mode != AnalysisMode.Commercial)
        {
            // Non-commercial modes have at most one plugin row per item and mode; it is the
            // unambiguous counterpart even when Jellyfin's stored range has drifted.
            var row = pluginRows.FirstOrDefault(r => r.Type == mode);
            return row?.IsUserProvided;
        }

        var startSeconds = TimeSpan.FromTicks(snapshot.StartTicks).TotalSeconds;
        var endSeconds = TimeSpan.FromTicks(snapshot.EndTicks).TotalSeconds;
        var match = pluginRows.FirstOrDefault(r => r.Type == mode
            && Math.Abs(r.Start - startSeconds) <= IntroSkipperDatabase.SegmentComparisonEpsilon
            && Math.Abs(r.End - endSeconds) <= IntroSkipperDatabase.SegmentComparisonEpsilon);
        return match?.IsUserProvided;
    }
}
