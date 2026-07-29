using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;

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
public class MediaSegmentEditorService(IJellyfinSegmentStore segmentStore, MediaSegmentMirror mirror, IIntroSkipperDatabase database)
{
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly MediaSegmentMirror _mirror = mirror;
    private readonly IIntroSkipperDatabase _database = database;

    /// <summary>
    /// Mirrors the plugin database into Jellyfin's media segments for one item via the
    /// shared <see cref="MediaSegmentMirror"/>. Failures propagate to the caller.
    /// </summary>
    /// <param name="item">The media item to synchronize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SyncItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _mirror.SyncItemAsync(item.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a Jellyfin segment. No-op when mirroring is disabled.
    /// </summary>
    /// <param name="itemId">The Id of the item that owns the segment.</param>
    /// <param name="segmentId">The Id of the segment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        if (!MediaSegmentMirrorPolicy.Enabled)
        {
            return;
        }

        await _segmentStore.DeleteSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a stored segment end-to-end: removes the plugin row (tombstoning automatic
    /// segments, hard-deleting user rows), mirrors the delete to Jellyfin (a no-op when
    /// mirroring is disabled) rolling the plugin delete back when the mirror delete fails,
    /// and returns the episode to NotAnalyzed for the segment's mode so the next analysis
    /// run can re-detect remaining segments (the tombstone keeps the deleted range gone).
    /// </summary>
    /// <param name="itemId">The item that owns the segment.</param>
    /// <param name="mode">Analysis mode of the deleted segment, used for the season-state reset.</param>
    /// <param name="jellyfinSegmentId">The Jellyfin segment id to delete; an unknown id is a no-op there.</param>
    /// <param name="pluginSegmentId">The plugin row id, or <c>null</c> when no plugin-side counterpart exists
    /// (uncorrelated legacy Jellyfin rows) so only the Jellyfin delete and state reset run.</param>
    /// <param name="cancellationToken">Cancellation token; the rollback is deliberately uncancelable
    /// once the plugin delete has completed.</param>
    /// <returns>The deleted plugin row snapshot; <c>null</c> when a plugin row was addressed but no longer
    /// deletable (unknown or already suppressed — nothing else is touched then), or when no plugin row
    /// was addressed.</returns>
    public async Task<DbSegment?> DeleteStoredSegmentAsync(
        Guid itemId,
        AnalysisMode mode,
        Guid jellyfinSegmentId,
        Guid? pluginSegmentId,
        CancellationToken cancellationToken)
    {
        DbSegment? deleted = null;
        if (pluginSegmentId is { } pluginRowId)
        {
            deleted = await _database.DeleteSegmentAsync(pluginRowId, cancellationToken).ConfigureAwait(false);
            if (deleted is null)
            {
                // The row vanished or was suppressed concurrently; whoever removed it owned the cascade.
                return null;
            }
        }

        try
        {
            await DeleteSegmentAsync(itemId, jellyfinSegmentId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _database.UndoDeleteAsync(deleted, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var item = Plugin.Instance!.GetItem(itemId);
        if (item is not null)
        {
            await _database.RemoveEpisodeIdAsync(SeasonStateKeyResolver.Resolve(item), mode, itemId, cancellationToken).ConfigureAwait(false);
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
    public async Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var segment = await _segmentStore.GetSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return segment;
    }
}
