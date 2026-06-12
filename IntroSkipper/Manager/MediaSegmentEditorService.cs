using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Performs targeted Jellyfin media-segment editor operations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentEditorService"/> class.
/// </remarks>
/// <param name="mediaSegmentManager">The Jellyfin <see cref="IMediaSegmentManager"/> used to edit segments.</param>
/// <param name="logger">Application logger.</param>
public partial class MediaSegmentEditorService(
    IMediaSegmentManager mediaSegmentManager,
    ILogger<MediaSegmentEditorService> logger)
{
    private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;
    private readonly ILogger<MediaSegmentEditorService> _logger = logger;
    private readonly LibraryOptions _externalProviders = new()
    {
        DisabledMediaSegmentProviders = ["Chapter Segments Provider"]
    };

    // One semaphore per item id; serialises concurrent CreateOrReplaceSegmentAsync calls for
    // the same item so the get-then-create sequence is effectively atomic.
    // RefCountedSemaphore tracks how many callers currently hold a reference so that entries
    // are pruned from the dictionary as soon as the last caller is done, preventing unbounded growth.
    private static readonly Dictionary<Guid, RefCountedSemaphore> _itemLocks = [];
    private static readonly object _itemLocksLock = new();

    /// <summary>
    /// Creates or replaces a Jellyfin media segment for the given item.
    /// Operates only on segments of the supplied type, leaving all other types untouched.
    /// </summary>
    /// <remarks>
    /// Non-commercial segments are replaced: any existing Jellyfin segment of the same type
    /// is deleted before the new one is created. Commercial segments are deduplicated by
    /// start/end ticks: the new segment is only created when no identical entry already exists.
    /// </remarks>
    /// <param name="item">The media item that owns the segment.</param>
    /// <param name="segment">The segment DTO to persist in Jellyfin's database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task CreateOrReplaceSegmentAsync(BaseItem item, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        var providerEntry = _mediaSegmentManager
            .GetSupportedProviders(item)
            .FirstOrDefault(p => string.Equals(p.Name, Plugin.Instance!.Name, StringComparison.OrdinalIgnoreCase));

        if (providerEntry == default)
        {
            LogProviderNotFound(_logger, item.Id);
            return;
        }

        var lockEntry = RentItemLock(item.Id);
        var acquired = false;
        try
        {
            await lockEntry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            var existingSegments = await _mediaSegmentManager
                .GetSegmentsAsync(item, [segment.Type], _externalProviders, filterByProvider: false)
                .ConfigureAwait(false);

            if (segment.Type == MediaSegmentType.Commercial)
            {
                // Multiple commercial segments per item are valid; skip creation only when an
                // identical entry (same start and end) is already present.
                if (existingSegments.Any(e => e.StartTicks == segment.StartTicks && e.EndTicks == segment.EndTicks))
                {
                    return;
                }
            }
            else
            {
                // Only one segment of each non-commercial type is kept per item.
                // Deletes run in parallel; individual failures are logged but do not abort the others.
                await Task.WhenAll(existingSegments.Select(async e =>
                {
                    try
                    {
                        await _mediaSegmentManager.DeleteSegmentAsync(e.Id).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Do not swallow cancellation or critical exceptions.
                        if (ex is OperationCanceledException
                            || ex is OutOfMemoryException
                            || ex is StackOverflowException
                            || ex is ThreadAbortException
                            || ex is ThreadInterruptedException
                            || ex is AccessViolationException)
                        {
                            throw;
                        }

                        // Log and continue so that a failure deleting one segment
                        // does not prevent processing of other segments.
                        LogErrorDeletingSegment(_logger, ex, e.Id);
                    }
                })).ConfigureAwait(false);
            }

            segment.ItemId = item.Id;
            await _mediaSegmentManager.CreateSegmentAsync(segment, providerEntry.Id).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                lockEntry.Semaphore.Release();
            }

            ReturnItemLock(item.Id, lockEntry);
        }
    }

    /// <summary>
    /// Deletes a segment.
    /// </summary>
    /// <param name="segmentId">The Id of the segment.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteSegmentAsync(Guid segmentId)
    {
        await _mediaSegmentManager.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
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
        var item = Plugin.Instance?.GetItem(itemId);
        if (item is null)
        {
            LogItemNotFound(_logger, itemId);
            return null;
        }

        var segments = await _mediaSegmentManager
            .GetSegmentsAsync(item, null, _externalProviders, filterByProvider: false)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return segments.FirstOrDefault(segment => segment.Id == segmentId);
    }

    // Retrieve or create the per-item lock entry, incrementing its reference count so that the
    // entry is not removed from the dictionary until every caller has called ReturnItemLock.
    private static RefCountedSemaphore RentItemLock(Guid id)
    {
        lock (_itemLocksLock)
        {
            if (_itemLocks.TryGetValue(id, out var existing))
            {
                existing.RefCount++;
                return existing;
            }

            var entry = new RefCountedSemaphore();
            _itemLocks[id] = entry;
            return entry;
        }
    }

    // Decrement the reference count, remove the dictionary entry, and dispose the semaphore
    // when the last caller returns its reference.
    private static void ReturnItemLock(Guid id, RefCountedSemaphore entry)
    {
        lock (_itemLocksLock)
        {
            if (--entry.RefCount == 0)
            {
                _itemLocks.Remove(id);
                entry.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Item not found for episode {EpisodeId}")]
    private static partial void LogItemNotFound(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intro Skipper provider entry not found for item {ItemId}; Jellyfin segment will not be created")]
    private static partial void LogProviderNotFound(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error deleting segment {SegmentId}")]
    private static partial void LogErrorDeletingSegment(ILogger logger, Exception ex, Guid segmentId);

    // Pairs a SemaphoreSlim with a caller reference count so that the dictionary entry can be
    // pruned and the semaphore disposed exactly when the last caller returns its reference.
    // RefCount is always read and written under _itemLocksLock.
    private sealed class RefCountedSemaphore : IDisposable
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int RefCount { get; set; } = 1;

        public void Dispose() => Semaphore.Dispose();
    }
}
