using System.Collections.Concurrent;
using IntroSkipper.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Manager;

/// <summary>
/// Performs targeted Jellyfin media-segment editor operations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentEditorService"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments.</param>
/// <param name="segmentDtoFactory">Factory that converts stored plugin segments to Jellyfin DTOs.</param>
public class MediaSegmentEditorService(IJellyfinSegmentStore segmentStore, SegmentDtoFactory segmentDtoFactory)
{
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly SegmentDtoFactory _segmentDtoFactory = segmentDtoFactory;

    // Keyed semaphores are kept for the process lifetime; re-add eviction if touched item count becomes measurable.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = [];

    /// <summary>
    /// Mirrors the plugin database into Jellyfin's media segments for one item: every
    /// active plugin segment is pushed (carrying its plugin row id), and Intro Skipper
    /// rows no longer present in the plugin database are removed. Other providers'
    /// segments are never touched.
    /// </summary>
    /// <param name="item">The media item to synchronize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SyncItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var itemLock = _itemLocks.GetOrAdd(item.Id, static _ => new SemaphoreSlim(1, 1));
        await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var segments = await _segmentDtoFactory.CreateAsync(item.Id, cancellationToken).ConfigureAwait(false);
            await _segmentStore.ReplaceSegmentsAsync(item.Id, segments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            itemLock.Release();
        }
    }

    /// <summary>
    /// Deletes a segment.
    /// </summary>
    /// <param name="itemId">The Id of the item that owns the segment.</param>
    /// <param name="segmentId">The Id of the segment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        await _segmentStore.DeleteSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
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
