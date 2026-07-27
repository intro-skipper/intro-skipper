using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

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
/// <param name="logger">The application logger.</param>
public partial class MediaSegmentEditorService(
    IJellyfinSegmentStore segmentStore,
    IIntroSkipperDatabase database,
    IEnumerable<IMediaSegmentProvider> segmentProviders,
    ILogger<MediaSegmentEditorService> logger)
{
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly IEnumerable<IMediaSegmentProvider> _segmentProviders = segmentProviders;
    private readonly ILogger<MediaSegmentEditorService> _logger = logger;

    /// <summary>
    /// Creates or replaces one editor segment in both stores.
    /// </summary>
    /// <remarks>
    /// The operation waits asynchronously for the item's mutation lock. It commits the
    /// plugin row first, then writes Jellyfin: a non-commercial segment is authoritative
    /// for its whole type and is handed to <see cref="ReplaceEditorSegmentsAsync"/>, while
    /// a commercial segment is added only when no entry with the same start and end ticks
    /// exists. If the Jellyfin write fails, the plugin database is restored to its prior
    /// state without honoring cancellation, mirroring the replace and delete paths.
    /// </remarks>
    /// <param name="item">The media item that owns the segment.</param>
    /// <param name="seasonStateKey">The key under which the item's analyzed-state lists are stored.</param>
    /// <param name="segment">The segment to persist, expressed in ticks.</param>
    /// <param name="cancellationToken">The token that cancels waiting or work before both stores commit.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before both stores commit.</exception>
    public async Task CreateOrReplaceSegmentAsync(BaseItem item, Guid seasonStateKey, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(segment);

        var mode = AnalysisHelpers.MapSegmentTypeToMode(segment.Type);

        // A non-commercial write is authoritative for its whole type, which is exactly what
        // the replace path does for one mode. Delegating happens before the lock is taken:
        // the item's semaphore is not reentrant.
        if (segment.Type != MediaSegmentType.Commercial)
        {
            await ReplaceEditorSegmentsAsync(item, seasonStateKey, [segment], [mode], cancellationToken).ConfigureAwait(false);
            return;
        }

        var dbSegment = ToPluginSegment(item.Id, segment.StartTicks, segment.EndTicks);

        using var itemLock = await MediaSegmentItemLock.AcquireAsync(item.Id, cancellationToken).ConfigureAwait(false);

        // Add-if-absent: the update itself reports atomically whether this call inserted
        // the row. A pre-read could not establish that ownership: analyzers and the skip
        // controller write the plugin database without the item lock, so an equivalent
        // row can appear between a read and the write, and compensating based on the
        // read would delete the concurrent writer's row.
        var inserted = await _database
            .UpdateTimestampAsync(dbSegment, mode, isUserProvided: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _segmentStore.CreateCommercialIfAbsentAsync(item.Id, segment, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (inserted)
            {
                try
                {
                    await _database.DeleteTimestampAsync(item.Id, mode, dbSegment, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreException) when (!restoreException.IsCritical())
                {
                    LogRestoreFailed(_logger, restoreException, item.Id);
                }
            }

            throw;
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
                ToPluginSegment(item.Id, segment.StartTicks, segment.EndTicks),
                AnalysisHelpers.MapSegmentTypeToMode(segment.Type),
                isUserProvided: true))
            .ToList();
        var mappedTypes = targetModes.Select(mode => AnalysisHelpers.ModeToSegmentType[mode]).ToList();

        using var itemLock = await MediaSegmentItemLock.AcquireAsync(item.Id, cancellationToken).ConfigureAwait(false);
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
            // A failed restore is logged and swallowed so the original Jellyfin
            // failure, the actionable root cause, is the exception that surfaces.
            try
            {
                await _database
                    .ReplaceItemSegmentsAsync(item.Id, targetModes, priorRows, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception restoreException) when (!restoreException.IsCritical())
            {
                LogRestoreFailed(_logger, restoreException, item.Id);
            }

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
    /// Builds the authoritative segment set to apply to every target of a copy.
    /// </summary>
    /// <remarks>
    /// Selects the source rows that can be applied as a replacement, shifts them, and
    /// validates the result. Types the source does not carry are deliberately absent from
    /// the returned modes: the replacement is authoritative, so including them would delete
    /// the targets' segments of those types, user edits and other providers' rows included.
    /// </remarks>
    /// <param name="sourceItemId">The ID of the item to copy from.</param>
    /// <param name="requestedTypes">The types to copy, or <see langword="null"/> for every editor-managed type present on the source.</param>
    /// <param name="timeShiftTicks">Ticks added to every copied position.</param>
    /// <param name="cancellationToken">The token that cancels the source read.</param>
    /// <returns>
    /// The first validation error, or the segments to write and the modes they replace.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled while reading.</exception>
    public async Task<(string? Error, List<MediaSegmentDto> Segments, List<AnalysisMode> Modes)> BuildCopySegmentsAsync(
        Guid sourceItemId,
        IReadOnlySet<MediaSegmentType>? requestedTypes,
        long timeShiftTicks,
        CancellationToken cancellationToken)
    {
        var sourceView = await GetEditorSegmentsAsync(sourceItemId, cancellationToken).ConfigureAwait(false);

        var copiedSegments = new List<MediaSegmentDto>();
        var copiedCommercialRanges = new List<(long Start, long End)>();
        foreach (var segment in SelectCopySources(sourceView, requestedTypes))
        {
            long startTicks;
            long endTicks;
            try
            {
                startTicks = checked(segment.StartTicks + timeShiftTicks);
                endTicks = checked(segment.EndTicks + timeShiftTicks);
            }
            catch (OverflowException)
            {
                return ($"Time shift overflows the tick range for a '{segment.Type}' segment.", [], []);
            }

            if (endTicks <= 0)
            {
                // Under replace semantics, dropping this segment would silently delete that
                // type on every target; reject the request instead.
                return ($"Time shift moves a '{segment.Type}' segment entirely before the item start.", [], []);
            }

            // A negative shift may push a segment's start before zero; clamp instead of
            // failing so a plain "shift everything earlier" request stays usable.
            var clampedStart = Math.Max(0, startTicks);

            if (endTicks <= clampedStart)
            {
                return ($"Time shift produces an empty '{segment.Type}' segment.", [], []);
            }

            if (segment.Type == MediaSegmentType.Commercial)
            {
                var range = (clampedStart, endTicks);
                if (copiedCommercialRanges.Any(existing => AreCommercialRangesEquivalent(existing, range)))
                {
                    // Cross-provider near-duplicates were already collapsed during
                    // selection, and a uniform shift preserves distances, so only start
                    // clamping can push two ranges within the comparison tolerance here.
                    // Keep the first instead of failing the request.
                    continue;
                }

                copiedCommercialRanges.Add(range);
            }

            copiedSegments.Add(new MediaSegmentDto
            {
                Type = segment.Type,
                StartTicks = clampedStart,
                EndTicks = endTicks,
            });
        }

        if (copiedSegments.Count == 0)
        {
            // Applying an empty authoritative set would erase the requested types on every
            // target; an empty source selection is almost certainly a caller error.
            return ("The source item has no segments of the requested types.", [], []);
        }

        var copyModes = copiedSegments
            .Select(segment => AnalysisHelpers.MapSegmentTypeToMode(segment.Type))
            .Distinct()
            .ToList();

        return (null, copiedSegments, copyModes);
    }

    /// <summary>
    /// Validates a segment set and returns the entries that should actually be written.
    /// </summary>
    /// <remarks>
    /// Exact-duplicate commercial entries are dropped rather than rejected, mirroring the
    /// create path's identical-entry semantics; the validator already recognizes them, so it
    /// also collects what survives instead of leaving a second pass to re-derive it.
    /// </remarks>
    /// <param name="segments">The submitted segments.</param>
    /// <returns>The first validation error, or the normalized set when the input is valid.</returns>
    public static (string? Error, List<MediaSegmentDto> Normalized) ValidateSegmentSet(IReadOnlyList<MediaSegmentDto> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var seenNonCommercialModes = new HashSet<AnalysisMode>();
        var seenCommercialRanges = new List<(long Start, long End)>();
        var seenIds = new HashSet<Guid>();
        var normalized = new List<MediaSegmentDto>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                return ("Segment entries must not be null.", []);
            }

            // A supplied id becomes the Jellyfin row's primary key, so a repeat would fail
            // the insert mid-transaction and surface as a server error rather than a client
            // one. The empty guid is exempt: it is how callers ask for a generated id, and
            // every new segment carries it.
            if (segment.Id != Guid.Empty && !seenIds.Add(segment.Id))
            {
                return ($"Segment id '{segment.Id}' appears more than once.", []);
            }

            if (!AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type, out var mode))
            {
                return ($"Segment type '{segment.Type}' is not supported by the editor.", []);
            }

            if (segment.StartTicks < 0)
            {
                return ($"Segment start must not be negative (type '{segment.Type}').", []);
            }

            if (segment.EndTicks <= segment.StartTicks)
            {
                return ($"Segment end must be after its start (type '{segment.Type}').", []);
            }

            if (mode == AnalysisMode.Commercial)
            {
                var range = (segment.StartTicks, segment.EndTicks);
                if (seenCommercialRanges.Contains(range))
                {
                    // Exact duplicates are dropped rather than written twice.
                    continue;
                }

                if (seenCommercialRanges.Any(existing => AreCommercialRangesEquivalent(existing, range)))
                {
                    return ("Commercial segment ranges must differ by more than the comparison tolerance.", []);
                }

                seenCommercialRanges.Add(range);
                normalized.Add(segment);
                continue;
            }

            if (!seenNonCommercialModes.Add(mode))
            {
                return ($"Only one segment of type '{segment.Type}' is allowed per item.", []);
            }

            normalized.Add(segment);
        }

        return (null, normalized);
    }

    /// <summary>
    /// Selects source segments that can be safely applied as an authoritative replacement.
    /// </summary>
    /// <remarks>
    /// Jellyfin permits several providers to hold a segment of one type, but the plugin
    /// permits only one non-commercial segment per type. The result therefore contains one
    /// non-commercial segment per type: Intro Skipper's row wins, then the earliest start.
    /// Commercial rows are kept from every provider, except that rows equivalent within
    /// the plugin's comparison tolerance collapse onto one entry with the same preference
    /// order. This prevents a replacement set that violates the plugin's unique index or
    /// its commercial-equivalence guard.
    /// </remarks>
    /// <param name="sourceView">The source item's cross-provider segment view.</param>
    /// <param name="requestedTypes">The types to copy, or <see langword="null"/> for every editor-managed type present on the source.</param>
    /// <returns>The selected segments ordered by start position.</returns>
    private static List<EditorSegmentDto> SelectCopySources(
        IReadOnlyList<EditorSegmentDto> sourceView,
        IReadOnlySet<MediaSegmentType>? requestedTypes)
    {
        var candidates = sourceView
            .Where(segment => AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type, out _))
            .Where(segment => requestedTypes is null || requestedTypes.Contains(segment.Type));

        // Intro Skipper's own row wins, then the earliest start.
        static IEnumerable<EditorSegmentDto> ByPreference(IEnumerable<EditorSegmentDto> group)
            => group
                .OrderByDescending(segment => string.Equals(segment.ProviderId, JellyfinSegmentStore.ProviderId, StringComparison.Ordinal))
                .ThenBy(segment => segment.StartTicks);

        var selected = new List<EditorSegmentDto>();
        foreach (var group in candidates.GroupBy(segment => segment.Type))
        {
            if (group.Key != MediaSegmentType.Commercial)
            {
                selected.Add(ByPreference(group).First());
                continue;
            }

            var chosen = new List<EditorSegmentDto>();
            foreach (var candidate in ByPreference(group))
            {
                if (!chosen.Any(existing => AreCommercialRangesEquivalent(
                    (existing.StartTicks, existing.EndTicks),
                    (candidate.StartTicks, candidate.EndTicks))))
                {
                    chosen.Add(candidate);
                }
            }

            selected.AddRange(chosen);
        }

        return selected.OrderBy(segment => segment.StartTicks).ToList();
    }

    /// <summary>
    /// Converts a Jellyfin segment's tick positions into the plugin's own segment shape,
    /// which stores positions as seconds.
    /// </summary>
    private static Segment ToPluginSegment(Guid itemId, long startTicks, long endTicks)
        => new(itemId, new TimeRange(
            TimeSpan.FromTicks(startTicks).TotalSeconds,
            TimeSpan.FromTicks(endTicks).TotalSeconds));

    private static bool AreCommercialRangesEquivalent(
        (long Start, long End) first,
        (long Start, long End) second)
        => IntroSkipperDatabase.TickRangesEquivalent(first.Start, first.End, second.Start, second.End);

    /// <summary>
    /// Lists Jellyfin media segment rows whose items no longer exist, grouped per item and
    /// split by owning provider. Includes rows keyed by the empty guid.
    /// </summary>
    /// <param name="itemExists">Predicate deciding whether an item id is a live library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The orphaned items.</returns>
    public async Task<IReadOnlyList<ItemSegmentCounts>> GetOrphanedSegmentsAsync(Func<Guid, bool> itemExists, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemExists);

        var counts = await _segmentStore.GetItemSegmentCountsAsync(cancellationToken).ConfigureAwait(false);

        return counts.Where(entry => !itemExists(entry.ItemId)).ToList();
    }

    /// <summary>
    /// Deletes Intro Skipper's Jellyfin segment rows for items that no longer exist. The
    /// orphan set is always recomputed because a caller-supplied list is time-of-check
    /// sensitive: a library scan can restore items between listing and deleting. Each
    /// item's delete runs under its mutation lock and re-checks the item's existence
    /// inside the lock, so a concurrently restored item is skipped rather than stripped
    /// of rows a refresh or editor write just produced. Other providers' rows and
    /// empty-guid rows are retained.
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

        var deletedItemCount = 0;
        foreach (var itemId in deletableIds)
        {
            // One lock at a time: holding many item locks simultaneously could deadlock
            // against callers acquiring them in a different order, and this is an admin
            // cleanup path where per-item statements are acceptable.
            using var itemLock = await MediaSegmentItemLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);

            // Re-checked inside the lock: a library scan can restore the item between
            // the orphan listing and this delete. A restored item is live again, so its
            // rows stay and it does not count as deleted.
            if (itemExists(itemId))
            {
                continue;
            }

            await _segmentStore.DeleteOwnSegmentsAsync([itemId], cancellationToken).ConfigureAwait(false);
            deletedItemCount++;
        }

        return deletedItemCount;
    }

    /// <summary>
    /// Deletes one editor segment from both stores.
    /// </summary>
    /// <remarks>
    /// The operation waits asynchronously for the item's mutation lock. For Intro
    /// Skipper-owned rows it removes the plugin row before deleting the Jellyfin row; if
    /// the Jellyfin delete fails, it restores exactly the removed plugin rows, and only
    /// those, without honoring cancellation. Another provider's row is deleted from
    /// Jellyfin alone: Intro Skipper's plugin rows are not its counterpart and stay
    /// untouched. A delete that changed Intro Skipper-owned state also clears the item's
    /// season-state entry, without honoring cancellation, so the next analysis can
    /// re-process the episode.
    /// </remarks>
    /// <param name="itemId">The ID of the item that owns the segment.</param>
    /// <param name="segmentId">The ID of the Jellyfin segment to delete.</param>
    /// <param name="mode">One of the analysis modes that specifies the expected segment type.</param>
    /// <param name="seasonStateKey">
    /// The item's season-state key, or <see langword="null"/> when the item is no longer in
    /// the library and there is no season entry to clear.
    /// </param>
    /// <param name="cancellationToken">The token that cancels waiting or work before the Jellyfin delete.</param>
    /// <returns>
    /// A result that reports whether deletion occurred and the actual mismatched type when a
    /// type conflict prevented deletion (or <see langword="null"/> when the segment was not
    /// found at all). An id that named no row in either store removed nothing, so it reports
    /// not-deleted for every mode rather than a success that would re-queue the episode.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before the Jellyfin delete.</exception>
    public async Task<(bool Deleted, MediaSegmentType? ActualType)> DeleteSegmentAsync(
        Guid itemId,
        Guid segmentId,
        AnalysisMode mode,
        Guid? seasonStateKey,
        CancellationToken cancellationToken)
    {
        using var itemLock = await MediaSegmentItemLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var existingSegment = await _segmentStore.GetSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
        if (existingSegment is not null
            && existingSegment.Type != AnalysisHelpers.ModeToSegmentType[mode])
        {
            return (false, existingSegment.Type);
        }

        var dbSegment = existingSegment is null
            ? null
            : ToPluginSegment(itemId, existingSegment.StartTicks, existingSegment.EndTicks);

        if (dbSegment is null && mode == AnalysisMode.Commercial)
        {
            return (false, null);
        }

        // The plugin rows mirror Intro Skipper's own Jellyfin rows only. Deleting
        // another provider's row must not clear them: for non-commercial modes that
        // would silently take Intro Skipper's segment of the same type with it (the
        // zombie Jellyfin row would be swept by the next refresh), and the item may
        // be re-queued for analysis the user never asked for. A missing row keeps
        // the plugin-side delete as an escape hatch for cleaning up rows whose
        // Jellyfin counterpart is already gone.
        var isForeignRow = existingSegment is not null
            && !string.Equals(existingSegment.ProviderId, JellyfinSegmentStore.ProviderId, StringComparison.Ordinal);

        var ownCounterpartIds = existingSegment is null
            ? await ResolveOwnCounterpartIdsAsync(itemId, segmentId, mode, cancellationToken).ConfigureAwait(false)
            : [];

        // With no counterpart to sweep, still issue the no-op delete for the requested
        // id: it is the escape hatch's only contact with the store, so skipping it
        // would hide a store that is failing outright.
        IReadOnlyList<Guid> jellyfinDeleteIds = ownCounterpartIds.Count > 0 ? ownCounterpartIds : [segmentId];

        IReadOnlyList<DbSegment> deletedRows = [];
        if (!isForeignRow)
        {
            // Non-commercial modes have exactly one row per item and mode, so delete
            // that unambiguous counterpart even if Jellyfin's reported range has drifted.
            var deleteSegment = mode == AnalysisMode.Commercial ? dbSegment : null;
            deletedRows = await _database
                .DeleteTimestampAsync(itemId, mode, deleteSegment, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            // Deliberately uncancellable: the plugin row is already gone, so a token
            // canceled mid-delete could tear the two stores apart. Scoped to the item
            // so a stale segment id can never remove another item's segment.
            foreach (var jellyfinDeleteId in jellyfinDeleteIds)
            {
                await _segmentStore.DeleteSegmentAsync(itemId, jellyfinDeleteId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Once the plugin delete commits, compensation must finish even if the
            // request is canceled. Restore exactly what was removed and nothing more:
            // when no row was deleted there is no prior state to put back, so writing
            // the Jellyfin segment here would invent a plugin row that never existed
            // (a foreign provider's segment has no counterpart to begin with).
            await RestoreDeletedRowsAsync(itemId, mode, deletedRows).ConfigureAwait(false);
            throw;
        }

        // Nothing existed in either store: the id names no row of this item, no
        // re-minted counterpart resolved, and there was no plugin row to sweep. Report
        // that as missing like the commercial path above, rather than as a success that
        // changed nothing and re-queues the episode for analysis.
        if (existingSegment is null && ownCounterpartIds.Count == 0 && deletedRows.Count == 0)
        {
            return (false, null);
        }

        // Both stores have committed. Complete derived season bookkeeping even if the
        // request is canceled now so the next analysis can re-process the episode. Deleting
        // another provider's row changes nothing Intro Skipper owns, so it must not mark the
        // episode as needing re-analysis.
        if (!isForeignRow && seasonStateKey is Guid key)
        {
            await _database.RemoveEpisodeIdAsync(key, mode, itemId, CancellationToken.None).ConfigureAwait(false);
        }

        return (true, null);
    }

    /// <summary>
    /// Resolves the Intro Skipper-owned rows a stale segment id should stand in for. Segment
    /// ids are not stable: every refresh deletes and re-inserts Intro Skipper's rows, so
    /// Jellyfin mints a new id and an id the editor still holds resolves to nothing while the
    /// counterpart row is alive under a new one. Deleting that stale id would be a no-op and
    /// leave the row behind after the plugin row is gone, so the counterpart is resolved by
    /// type instead. Only non-commercial modes reach this path, and they have exactly one
    /// Intro Skipper-owned row per type.
    /// </summary>
    /// <remarks>
    /// An id that is still alive under a <em>different</em> item is not stale, so it resolves
    /// to nothing here. Without that check, pairing one item's segment id with another item's
    /// id would sweep the second item's own rows of the requested type — the opposite of the
    /// documented contract that a foreign id leaves Jellyfin untouched.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> ResolveOwnCounterpartIdsAsync(
        Guid itemId,
        Guid segmentId,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (await _segmentStore.SegmentIdExistsAsync(segmentId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var itemRows = await _segmentStore.GetItemSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        return itemRows
            .Where(row => row.Type == AnalysisHelpers.ModeToSegmentType[mode]
                && string.Equals(row.ProviderId, JellyfinSegmentStore.ProviderId, StringComparison.Ordinal))
            .Select(row => row.Id)
            .ToList();
    }

    /// <summary>
    /// Puts back the plugin rows a failed Jellyfin delete left orphaned. Deliberately
    /// uncancellable, and a failed restore is logged and swallowed so the original Jellyfin
    /// failure, the actionable root cause, is the exception that surfaces.
    /// </summary>
    private async Task RestoreDeletedRowsAsync(Guid itemId, AnalysisMode mode, IReadOnlyList<DbSegment> deletedRows)
    {
        if (deletedRows.Count == 0)
        {
            return;
        }

        try
        {
            if (mode == AnalysisMode.Commercial)
            {
                // The delete was range-scoped, so other commercials survived and only the
                // removed rows are added back. Commercial writes take UpdateTimestampAsync's
                // add-if-absent branch; the inserted-or-not report is irrelevant here.
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
            else
            {
                // The delete removed every row of the mode, so the mode is restored as a
                // whole. This must not go through UpdateTimestampAsync: that path carries
                // detection-time rules (it refuses to overwrite a user-provided row, and
                // refuses a detected Credits row overlapping a stored Introduction) which
                // would silently drop the restore and leave the two stores torn apart.
                await _database
                    .ReplaceItemSegmentsAsync(itemId, [mode], deletedRows, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception restoreException) when (!restoreException.IsCritical())
        {
            LogRestoreFailed(_logger, restoreException, itemId);
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
            && IntroSkipperDatabase.RangesEquivalent(r.Start, r.End, startSeconds, endSeconds));
        return match?.IsUserProvided;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to restore the plugin database after a Jellyfin segment write failure for item {ItemId}; the stores may disagree until the next segment refresh.")]
    private static partial void LogRestoreFailed(ILogger logger, Exception exception, Guid itemId);
}
