using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Owns every interactive media-segment mutation end-to-end: plugin-database write,
/// Jellyfin mirror convergence, and failure handling. Mutations for the same item are
/// serialized on a striped lock, so each write-plus-mirror(-plus-rollback) sequence is
/// linearizable per item; controllers only validate requests and map results to HTTP.
/// Mirror writes honor <see cref="MediaSegmentMirrorPolicy"/> and no-op when mirroring
/// is disabled, so callers never gate them.
/// <para>
/// On a mirror failure: create/update/restore keep the committed row (the plugin
/// database is the source of truth; a retry or later sync converges the mirror),
/// delete rolls the plugin delete back (a hard-deleted user row is unrecoverable),
/// and the disable toggle rolls the flag back (the flag must not disagree with what
/// Jellyfin serves). All failures propagate so responses never claim success.
/// </para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentEditorService"/> class.
/// Must be registered as a singleton so the mutation stripes are shared by all requests.
/// </remarks>
/// <param name="mirror">The shared locked mirror write path; the editor's only path to
/// Jellyfin's media segments.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="logger">Application logger.</param>
public partial class MediaSegmentEditorService(
    MediaSegmentMirror mirror,
    IIntroSkipperDatabase database,
    ILogger<MediaSegmentEditorService> logger)
{
    // Serializes all editor mutations per item, above the mirror's stripes: a concurrent
    // editor mutation's sync between another request's plugin write and its rollback
    // would bake the rolled-back state into the mirror. Separate pool from
    // MediaSegmentMirror's; lock order is always mutation stripe -> mirror stripe.
    // Non-editor syncs (bulk refreshes, the legacy Timestamps shim) take only mirror
    // stripes, so they can delay, never deadlock, a mutation. One of them can still
    // interleave between a write and its rollback and briefly publish the pre-rollback
    // state; the next sync converges it.
    private readonly StripedAsyncLock _mutationLock = new();

    private readonly MediaSegmentMirror _mirror = mirror;
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
            await _database.SetItemDisabledAsync(seasonKey, item.Id, previous, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes a stored segment whose plugin row shares its id with the Jellyfin row:
    /// tombstones automatic segments, hard-deletes user rows, mirrors the delete, and
    /// resets the season state for the row's mode so re-analysis can run (the tombstone
    /// keeps the deleted range gone).
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="segmentId">The shared plugin/Jellyfin segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deleted plugin row snapshot, or <c>null</c> when the row is unknown on
    /// the item or already suppressed (nothing is touched then).</returns>
    public async Task<DbSegment?> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
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
    /// shared-id scheme, or foreign-provider rows). The plugin counterpart is matched by
    /// mode and exact ticks; without one, only the Jellyfin delete and state reset run.
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="mode">The segment's mode; drives the season-state reset.</param>
    /// <param name="jellyfinSegment">The Jellyfin row to delete; the caller resolves and validates it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteUncorrelatedSegmentAsync(Guid itemId, AnalysisMode mode, MediaSegmentDto jellyfinSegment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jellyfinSegment);

        using var stripe = await _mutationLock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var itemRows = await _database.GetSegmentsAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var match = itemRows.FirstOrDefault(s => s.Type == mode
            && s.StartTicks == jellyfinSegment.StartTicks
            && s.EndTicks == jellyfinSegment.EndTicks);

        DbSegment? deleted = null;
        if (match is not null)
        {
            deleted = await _database.DeleteSegmentAsync(itemId, match.Id, cancellationToken).ConfigureAwait(false);
            if (deleted is null)
            {
                // Vanished or suppressed concurrently; whoever removed it owned the
                // cascade.
                return;
            }
        }

        await DeleteMirrorRowAndResetStateAsync(itemId, mode, jellyfinSegment.Id, deleted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared tail of both delete flows: targeted Jellyfin delete through the mirror
    /// (rolling any plugin delete back on failure; uncancelable once started) and the
    /// season-state reset. A deleted plugin row whose Jellyfin row is not found signals
    /// drift (a concurrent refresh removed it first, or the server stopped preserving
    /// provider-supplied ids), so the item's mirror is re-synced with a warning. Once
    /// the Jellyfin delete has committed, the season-state reset runs uncancelably: the
    /// deleted row cannot be deleted again, so a canceled reset could never be retried
    /// and would strand the item in the analyzed set.
    /// </summary>
    private async Task DeleteMirrorRowAndResetStateAsync(Guid itemId, AnalysisMode resetMode, Guid jellyfinSegmentId, DbSegment? deletedPluginRow, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _mirror.DeleteSegmentAsync(itemId, jellyfinSegmentId, cancellationToken).ConfigureAwait(false);
            if (deletedPluginRow is not null && outcome == MirrorDeleteOutcome.RowNotFound)
            {
                LogJellyfinRowMissingOnDelete(_logger, jellyfinSegmentId, itemId);
                await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await _database.UndoDeleteAsync(deletedPluginRow, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var item = Plugin.Instance!.GetItem(itemId);
        if (item is not null)
        {
            // Uncancelable: the deletes above are committed and a retried request 404s
            // before reaching this repair, so honoring a cancellation here would leave
            // the item permanently marked analyzed and re-analysis skipping it.
            await _database.RemoveEpisodeIdAsync(SeasonStateKeyResolver.Resolve(item), resetMode, itemId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Jellyfin media segment row found under id {SegmentId} for item {ItemId}; re-syncing the item's mirror. A concurrent refresh may have removed the row already; if this recurs without concurrent activity, the server may no longer preserve provider-supplied segment ids.")]
    private static partial void LogJellyfinRowMissingOnDelete(ILogger logger, Guid segmentId, Guid itemId);
}
