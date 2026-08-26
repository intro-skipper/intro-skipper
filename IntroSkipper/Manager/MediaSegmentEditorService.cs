using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Owns every interactive mutation end-to-end (the plural segments API, the
/// MediaSegmentsApi and <c>Timestamps</c> shims and the per-item disable toggle):
/// plugin-database write, Jellyfin mirror convergence, and failure handling. Mutations
/// for the same item are serialized on a striped lock, so each
/// write-plus-mirror(-plus-rollback) sequence is linearizable per item; controllers only
/// validate requests and map results to HTTP. Mirror writes honor
/// <see cref="MediaSegmentMirrorPolicy"/> and no-op when mirroring is disabled, so
/// callers never gate them.
/// <para>
/// On a mirror failure: create/update/restore keep the committed row (the plugin
/// database is the source of truth; a retry or later sync converges the mirror),
/// delete rolls the plugin delete back (a hard-deleted user row is unrecoverable),
/// and the disable toggle rolls the flag back (the flag must not disagree with what
/// Jellyfin serves). All failures propagate so responses never claim success; a
/// rollback that itself fails is logged and the mirror failure still propagates, so
/// the cause is never replaced by its own cleanup.
/// </para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentEditorService"/> class.
/// Must be registered as a singleton so the mutation stripes are shared by all requests.
/// </remarks>
/// <param name="mirror">The shared locked mirror write path; the editor's only write
/// path to Jellyfin's media segments.</param>
/// <param name="segmentStore">Direct store for Jellyfin's media segments; the legacy
/// delete dispatch reads it under the mutation stripe, all writes go through
/// <paramref name="mirror"/>.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="logger">Application logger.</param>
public partial class MediaSegmentEditorService(
    MediaSegmentMirror mirror,
    IJellyfinSegmentStore segmentStore,
    IIntroSkipperDatabase database,
    ILogger<MediaSegmentEditorService> logger)
{
    // See DeleteUncorrelatedSegmentCoreAsync: truncated (pre-upgrade mirror) vs rounded
    // (import) conversion of the same seconds value differs by at most one tick.
    private const long UncorrelatedTickTolerance = 1;

    // Serializes all editor mutations per item, above the mirror's stripes: a concurrent
    // editor mutation's sync between another request's plugin write and its rollback
    // would bake the rolled-back state into the mirror. Separate pool from
    // MediaSegmentMirror's; lock order is always mutation stripe -> mirror stripe.
    // Non-editor syncs (bulk refreshes) take only mirror stripes, so they can delay,
    // never deadlock, a mutation. One of them can still interleave between a write and
    // its rollback and briefly publish the pre-rollback state; the next sync converges it.
    private readonly StripedAsyncLock _mutationLock = new();

    private readonly MediaSegmentMirror _mirror = mirror;
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly ILogger<MediaSegmentEditorService> _logger = logger;

    /// <summary>
    /// Creates a user segment and converges the item's Jellyfin mirror. An exact-range
    /// collision promotes the existing row to a user segment.
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="mode">Segment mode.</param>
    /// <param name="startTicks">Start time in ticks.</param>
    /// <param name="endTicks">End time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row.</returns>
    public async Task<DbSegment> CreateUserSegmentAsync(Guid itemId, AnalysisMode mode, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var row = await _database.AddUserSegmentAsync(itemId, mode, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
        await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Replaces the stored segments of each given mode with one user segment (the
    /// replace-on-write contract of the deprecated <c>POST Episode/{id}/Timestamps</c>
    /// and non-commercial <c>POST MediaSegmentsApi/{itemId}</c> shims) in a single
    /// plugin transaction and converges the item's Jellyfin mirror.
    /// </summary>
    /// <param name="itemId">The item that owns the segments.</param>
    /// <param name="segmentsByMode">The user segment to store per mode, in ticks; each end must be after its start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ReplaceUserSegmentsAsync(Guid itemId, IReadOnlyDictionary<AnalysisMode, (long StartTicks, long EndTicks)> segmentsByMode, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        await _database.ReplaceUserSegmentsAsync(itemId, segmentsByMode, cancellationToken).ConfigureAwait(false);
        await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates a segment's boundaries (the segment becomes user-provided) and converges
    /// the item's Jellyfin mirror. Moving a segment exactly onto another segment of the
    /// same mode merges the two; the occupant survives and is returned.
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="startTicks">New start time in ticks.</param>
    /// <param name="endTicks">New end time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The surviving row, or <c>null</c> when the id is unknown on the item or
    /// suppressed (nothing is touched then).</returns>
    public async Task<DbSegment?> UpdateSegmentAsync(Guid itemId, Guid segmentId, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var updated = await _database.UpdateSegmentAsync(itemId, segmentId, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return null;
        }

        await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Restores a tombstoned segment, making it active again with its original source,
    /// and converges the item's Jellyfin mirror.
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The restored row, or <c>null</c> when the id is unknown on the item or not
    /// suppressed (nothing is touched then).</returns>
    public async Task<DbSegment?> RestoreSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var restored = await _database.RestoreSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
        if (restored is null)
        {
            return null;
        }

        await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        return restored;
    }

    /// <summary>
    /// Sets the item's disable flag and strictly resyncs its Jellyfin mirror: disabling
    /// strips the automatic rows, enabling restores them from the untouched plugin rows.
    /// The resync runs even when the flag write was a no-op, so retrying after a failed
    /// rollback still repairs the mirror. The rollback is uncancelable once started.
    /// </summary>
    /// <param name="item">The library item; the caller resolves and validates it.</param>
    /// <param name="disabled">Whether the item's automatic segments are withheld from Jellyfin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SetItemDisabledAsync(BaseItem item, bool disabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The row's season key is a server-side pruning detail; callers only name the item.
        var seasonKey = SeasonStateKeyResolver.Resolve(item);

        using var stripe = await _mutationLock.AcquireAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var previous = await _database.SetItemDisabledAsync(seasonKey, item.Id, disabled, cancellationToken).ConfigureAwait(false);

        try
        {
            await _mirror.SyncItemAsync(item.Id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The mirror kept its old rows, so restore the stored flag. The held
            // stripe guarantees `previous` is still the value this request replaced.
            // A failing rollback is logged rather than thrown: the mirror failure is
            // the cause the caller has to see, and swapping it out would also hide
            // that the flag is now stuck at the requested value.
            try
            {
                await _database.SetItemDisabledAsync(seasonKey, item.Id, previous, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                LogFailedToRollBackDisabledFlag(_logger, rollbackException, item.Id, previous);
            }

            throw;
        }
    }

    /// <summary>
    /// Deletes a stored segment whose plugin row shares its id with the Jellyfin row:
    /// tombstones automatic segments, hard-deletes user rows, mirrors the delete, and
    /// clears the item's analysis record for the row's mode so re-analysis can run (the
    /// tombstone keeps the deleted range gone).
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="segmentId">The shared plugin/Jellyfin segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deleted plugin row snapshot, or <c>null</c> when the row is unknown on
    /// the item or already suppressed (nothing is touched then).</returns>
    public async Task<DbSegment?> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        return await DeleteSegmentCoreAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the Jellyfin-visible segment the legacy <c>DELETE MediaSegmentsApi/{segmentId}</c>
    /// shim names. The id is resolved and dispatched under the item's mutation stripe, so
    /// a concurrent mutation cannot invalidate the chosen path: a plugin row sharing the
    /// id runs the <see cref="DeleteSegmentAsync"/> cascade; a plugin row already
    /// tombstoned is an idempotent success whose mirror is re-synced, so a ghost Jellyfin
    /// row (re-added by Jellyfin's own provider run from a read predating the delete, see
    /// <see cref="MediaSegmentMirror"/>) is removed instead of reporting not-found
    /// forever; an id with no plugin row deletes the uncorrelated Jellyfin row and its
    /// tick- or mode-matched plugin counterpart.
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="mode">The mode the caller claims the segment has; a contradicting row is reported, not touched.</param>
    /// <param name="segmentId">The Jellyfin segment id (shared with the plugin row when one exists).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome: deleted, not found, or a type contradiction.</returns>
    public async Task<LegacySegmentDeleteResult> DeleteLegacySegmentAsync(Guid itemId, AnalysisMode mode, Guid segmentId, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);

        var pluginRow = await _database.GetSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (pluginRow is not null && pluginRow.ItemId == itemId)
        {
            if (pluginRow.Type != mode)
            {
                return LegacySegmentDeleteResult.TypeMismatch(AnalysisHelpers.ModeToSegmentType[pluginRow.Type]);
            }

            if (pluginRow.State == SegmentState.Suppressed)
            {
                // The plugin already treats the row as deleted, so the delete is
                // idempotently satisfied; converging the mirror removes any ghost row
                // Jellyfin re-added (a no-op when nothing lingers).
                await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                return LegacySegmentDeleteResult.Deleted;
            }

            var deleted = await DeleteSegmentCoreAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);

            // A null means the row vanished between the read and the delete; the
            // non-stripe writer that removed it owned the cascade.
            return deleted is null ? LegacySegmentDeleteResult.NotFound : LegacySegmentDeleteResult.Deleted;
        }

        var jellyfinSegment = await _segmentStore.GetSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
        if (jellyfinSegment is null)
        {
            return LegacySegmentDeleteResult.NotFound;
        }

        if (jellyfinSegment.Type != AnalysisHelpers.ModeToSegmentType[mode])
        {
            return LegacySegmentDeleteResult.TypeMismatch(jellyfinSegment.Type);
        }

        await DeleteUncorrelatedSegmentCoreAsync(itemId, mode, jellyfinSegment, cancellationToken).ConfigureAwait(false);
        return LegacySegmentDeleteResult.Deleted;
    }

    /// <summary>
    /// Shared-id delete cascade under the caller-held mutation stripe: item-scoped plugin
    /// delete, targeted Jellyfin delete with rollback, and the analysis-record clear.
    /// </summary>
    private async Task<DbSegment?> DeleteSegmentCoreAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var deleted = await _database.DeleteSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
        if (deleted is null)
        {
            // Unknown on the item, or vanished/suppressed concurrently; whoever
            // removed it owned the cascade.
            return null;
        }

        await DeleteMirrorRowAndResetStateAsync(itemId, deleted.Type, segmentId, deleted, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Deletes a Jellyfin segment row with no shared plugin id (rows predating the
    /// shared-id scheme, or foreign-provider rows) under the caller-held mutation stripe.
    /// The plugin counterpart is matched by mode and ticks (one tick of tolerance, see
    /// <see cref="UncorrelatedTickTolerance"/>), falling back for non-commercial modes to
    /// the item's single active row of the mode; without a counterpart, only the Jellyfin
    /// delete and state reset run. A matched counterpart may already be mirrored under
    /// its own id, so the item's mirror is re-synced after the targeted delete.
    /// </summary>
    private async Task DeleteUncorrelatedSegmentCoreAsync(Guid itemId, AnalysisMode mode, MediaSegmentDto jellyfinSegment, CancellationToken cancellationToken)
    {
        var itemRows = await _database.GetSegmentsAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Rows mirrored before the shared-id scheme were converted from seconds by
        // truncation while the legacy import rounds, so the two can sit one tick apart;
        // absorb that here without reintroducing range-level epsilon matching elsewhere.
        var match = itemRows.FirstOrDefault(s => s.Type == mode
            && Math.Abs(s.StartTicks - jellyfinSegment.StartTicks) <= UncorrelatedTickTolerance
            && Math.Abs(s.EndTicks - jellyfinSegment.EndTicks) <= UncorrelatedTickTolerance);

        // A Jellyfin row can drift from its plugin counterpart when re-analysis or edits
        // ran while mirroring was off. The legacy DELETE wire matched mode-wide for
        // non-commercial types, so honor that where it is unambiguous — exactly one
        // active row of the mode; commercials (many per item) keep exact matching.
        if (match is null && mode != AnalysisMode.Commercial)
        {
            var modeRows = itemRows.Where(s => s.Type == mode).ToList();
            if (modeRows.Count == 1)
            {
                match = modeRows[0];
            }
        }

        DbSegment? deleted = null;
        if (match is not null)
        {
            // A null here means the row vanished or was suppressed concurrently by a
            // non-stripe writer (analysis or a bulk erase). That writer's cascade only
            // ever targets the plugin row's own id, never the uncorrelated Jellyfin id
            // the caller named — so fall through and delete the Jellyfin row regardless.
            deleted = await _database.DeleteSegmentAsync(itemId, match.Id, cancellationToken).ConfigureAwait(false);
        }

        await DeleteMirrorRowAndResetStateAsync(itemId, mode, jellyfinSegment.Id, deleted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared tail of both delete flows: targeted Jellyfin delete through the mirror
    /// (rolling any plugin delete back only when that targeted delete fails;
    /// uncancelable once started) and the analysis-record clear. The item's mirror is
    /// re-synced whenever the targeted delete cannot have covered the deleted plugin
    /// row: its shared id found no Jellyfin row (drift — a concurrent refresh removed
    /// it first, or the server stopped preserving provider-supplied ids — logged as a
    /// warning), or the delete targeted an uncorrelated Jellyfin id while the plugin
    /// row, matched by ticks, may still be mirrored under its own id. Once the Jellyfin
    /// delete has committed the deletes are final: the analysis-record clear and the
    /// re-sync run uncancelably (a retried request 404s before reaching either repair),
    /// and a failing re-sync propagates without rollback (the next sync of the item
    /// converges the mirror).
    /// </summary>
    private async Task DeleteMirrorRowAndResetStateAsync(Guid itemId, AnalysisMode resetMode, Guid jellyfinSegmentId, DbSegment? deletedPluginRow, CancellationToken cancellationToken)
    {
        MirrorDeleteOutcome outcome;
        try
        {
            outcome = await _mirror.DeleteSegmentAsync(itemId, jellyfinSegmentId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // As in SetItemDisabledAsync: a failing rollback must not replace the mirror
            // failure that caused it, or the log loses both the real cause and the fact
            // that the plugin row stayed deleted.
            try
            {
                await _database.UndoDeleteAsync(deletedPluginRow, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                LogFailedToRollBackSegmentDelete(_logger, rollbackException, jellyfinSegmentId, itemId);
            }

            throw;
        }

        if (deletedPluginRow is not null && outcome == MirrorDeleteOutcome.RowNotFound)
        {
            LogJellyfinRowMissingOnDelete(_logger, jellyfinSegmentId, itemId);
        }

        // Uncancelable: the deletes above are committed and a retried request 404s
        // before reaching this repair, so honoring a cancellation here would leave
        // the item permanently marked analyzed and re-analysis skipping it.
        await _database.ClearItemAnalysisAsync(itemId, resetMode, CancellationToken.None).ConfigureAwait(false);

        // The deletes are committed, so a failure here must NOT restore the plugin row
        // (that would resurrect a segment the user deleted, while the Jellyfin row it
        // shared an id with is already gone). Propagate instead; the next sync of the
        // item converges any leftover mirrored row. Uncancelable for the same reason
        // as the analysis clear above: a retried request 404s before reaching this
        // repair, so honoring a cancellation would leave the stale mirrored row
        // visible until an unrelated sync of the item.
        if (deletedPluginRow is not null
            && outcome != MirrorDeleteOutcome.MirroringDisabled
            && (outcome == MirrorDeleteOutcome.RowNotFound || deletedPluginRow.Id != jellyfinSegmentId))
        {
            await _mirror.SyncItemAsync(itemId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll the disable flag of item {ItemId} back to {Previous} after a mirror failure; the item stays at the requested value while Jellyfin still serves the old rows. Re-issue the toggle to repair both.")]
    private static partial void LogFailedToRollBackDisabledFlag(ILogger logger, Exception exception, Guid itemId, bool previous);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to restore segment {SegmentId} of item {ItemId} after its Jellyfin delete failed; the plugin row stays deleted while Jellyfin still holds its row. The next mirror sync of the item converges them.")]
    private static partial void LogFailedToRollBackSegmentDelete(ILogger logger, Exception exception, Guid segmentId, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Jellyfin media segment row found under id {SegmentId} for item {ItemId}; re-syncing the item's mirror. A concurrent refresh may have removed the row already; if this recurs without concurrent activity, the server may no longer preserve provider-supplied segment ids.")]
    private static partial void LogJellyfinRowMissingOnDelete(ILogger logger, Guid segmentId, Guid itemId);
}
