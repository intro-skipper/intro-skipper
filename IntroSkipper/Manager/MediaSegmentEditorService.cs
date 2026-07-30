using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Enums;
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
public class MediaSegmentEditorService(IJellyfinSegmentStore segmentStore)
{
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;

    // Keyed semaphores are kept for the process lifetime; re-add eviction if touched item count becomes measurable.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = [];

    /// <summary>
    /// Creates or replaces a Jellyfin media segment for the given item.
    /// Operates only on segments of the supplied type, leaving all other types untouched.
    /// </summary>
    /// <remarks>
    /// Non-commercial segments are replaced atomically: any existing Jellyfin segment of the
    /// same type — regardless of provider — is deleted in the same transaction that creates
    /// the new one. Commercial segments are deduplicated by start/end ticks: the new segment
    /// is only created when no identical entry already exists.
    /// </remarks>
    /// <param name="item">The media item that owns the segment.</param>
    /// <param name="segment">The segment DTO to persist in Jellyfin's database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task CreateOrReplaceSegmentAsync(BaseItem item, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(segment);

        var itemLock = _itemLocks.GetOrAdd(item.Id, static _ => new SemaphoreSlim(1, 1));
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
    /// Retrieves a segment from Jellyfin by id without item scoping.
    /// </summary>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment, or <c>null</c> if not found.</returns>
    public async Task<MediaSegmentDto?> GetSegmentByIdAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        var segment = await _segmentStore.GetSegmentByIdAsync(segmentId, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return segment;
    }
}
