using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Performs targeted Jellyfin media-segment editor operations. All write operations
/// honor <see cref="MediaSegmentMirrorPolicy"/>: when mirroring is disabled they are
/// no-ops, so callers never gate them.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentEditorService"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments.</param>
/// <param name="mirror">The shared locked mirror write path.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="logger">Application logger.</param>
public partial class MediaSegmentEditorService(
    IJellyfinSegmentStore segmentStore,
    MediaSegmentMirror mirror,
    IIntroSkipperDatabase database,
    ILogger<MediaSegmentEditorService> logger)
{
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly MediaSegmentMirror _mirror = mirror;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly ILogger<MediaSegmentEditorService> _logger = logger;

    /// <summary>
    /// Deletes a stored segment end-to-end: removes the plugin row (tombstoning automatic
    /// segments, hard-deleting user rows), mirrors the delete to Jellyfin (a no-op when
    /// mirroring is disabled) rolling the plugin delete back when the mirror write fails,
    /// and returns the episode to NotAnalyzed for the segment's mode so the next analysis
    /// run can re-detect remaining segments (the tombstone keeps the deleted range gone).
    /// When a plugin row was deleted but no Jellyfin row existed under the shared id, the
    /// item's mirror is re-synced with a warning: either the row was already gone, or the
    /// server no longer preserves provider-supplied ids — the re-sync keeps the mirror
    /// converged in both cases and makes the second loudly diagnosable from logs.
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="mode">Season-state reset mode for deletes that address no plugin row; when a
    /// plugin row is deleted its stored <see cref="DbSegment.Type"/> is authoritative and this
    /// parameter may be <c>null</c>.</param>
    /// <param name="jellyfinSegmentId">The Jellyfin segment id to delete; when unknown, a correlated
    /// plugin row triggers the warning re-sync and an uncorrelated delete is a no-op.</param>
    /// <param name="pluginSegmentId">The plugin row id, or <c>null</c> when no plugin-side counterpart exists
    /// (uncorrelated legacy Jellyfin rows) so only the Jellyfin delete and state reset run.</param>
    /// <param name="cancellationToken">Cancellation token; the rollback is deliberately uncancelable
    /// once the plugin delete has completed.</param>
    /// <returns>The deleted plugin row snapshot; <c>null</c> when a plugin row was addressed but no longer
    /// deletable (unknown or already suppressed — nothing else is touched then), or when no plugin row
    /// was addressed.</returns>
    public async Task<DbSegment?> DeleteStoredSegmentAsync(
        Guid itemId,
        AnalysisMode? mode,
        Guid jellyfinSegmentId,
        Guid? pluginSegmentId,
        CancellationToken cancellationToken)
    {
        DbSegment? deleted = null;
        if (pluginSegmentId is { } pluginRowId)
        {
            deleted = await _database.DeleteSegmentAsync(itemId, pluginRowId, cancellationToken).ConfigureAwait(false);
            if (deleted is null)
            {
                // Unknown on the item, or vanished/suppressed concurrently; whoever
                // removed it owned the cascade.
                return null;
            }
        }

        try
        {
            if (MediaSegmentMirrorPolicy.Enabled)
            {
                var jellyfinRowsDeleted = await _segmentStore.DeleteSegmentAsync(itemId, jellyfinSegmentId, cancellationToken).ConfigureAwait(false);
                if (deleted is not null && jellyfinRowsDeleted == 0)
                {
                    LogJellyfinRowMissingOnDelete(_logger, jellyfinSegmentId, itemId);
                    await _mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await _database.UndoDeleteAsync(deleted, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var item = Plugin.Instance!.GetItem(itemId);
        if (item is not null && (deleted?.Type ?? mode) is { } resetMode)
        {
            await _database.RemoveEpisodeIdAsync(SeasonStateKeyResolver.Resolve(item), resetMode, itemId, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>
    /// Retrieves a segment from Jellyfin by id.
    /// </summary>
    /// <param name="itemId">The item id that owns the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment, or <c>null</c> if not found.</returns>
    public Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
        => _segmentStore.GetSegmentAsync(itemId, segmentId, cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Jellyfin media segment row found under shared id {SegmentId} for item {ItemId}; re-syncing the item's mirror. If this recurs, the server may no longer preserve provider-supplied segment ids.")]
    private static partial void LogJellyfinRowMissingOnDelete(ILogger logger, Guid segmentId, Guid itemId);
}
