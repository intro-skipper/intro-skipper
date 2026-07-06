using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
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
/// <param name="libraryManager">The Jellyfin library manager used to resolve items by id.</param>
/// <param name="logger">Application logger.</param>
public partial class MediaSegmentEditorService(
    IMediaSegmentManager mediaSegmentManager,
    ILibraryManager libraryManager,
    ILogger<MediaSegmentEditorService> logger)
{
    private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSegmentEditorService> _logger = logger;

    // Keyed semaphores are kept for the process lifetime; re-add eviction if touched item count becomes measurable.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = [];

    /// <summary>
    /// Creates or replaces a Jellyfin media segment for the given item.
    /// Operates only on segments of the supplied type, leaving all other types untouched.
    /// </summary>
    /// <remarks>
    /// Single-entry segments are replaced: any existing Jellyfin segment of the same type
    /// is deleted before the new one is created. Commercial segments and movie credits are
    /// deduplicated by start/end ticks: the new segment is only created when no identical entry
    /// already exists.
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

        var itemLock = _itemLocks.GetOrAdd(item.Id, static _ => new SemaphoreSlim(1, 1));
        await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingSegments = await _mediaSegmentManager
                .GetSegmentsAsync(item, [segment.Type], MediaSegmentProviderDefaults.ExternalProviders, filterByProvider: false)
                .ConfigureAwait(false);

            if (MediaSegmentRules.AllowsMultipleSegments(segment.Type, item))
            {
                // Multiple matching segments per item are valid for commercials and movie credits;
                // skip creation only when an identical entry (same start and end) is already present.
                if (existingSegments.Any(e => e.StartTicks == segment.StartTicks && e.EndTicks == segment.EndTicks))
                {
                    return;
                }
            }
            else
            {
                // Only one segment of each single-entry type is kept per item.
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
            itemLock.Release();
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
        var item = itemId != Guid.Empty ? _libraryManager.GetItemById(itemId) : null;
        if (item is null)
        {
            LogItemNotFound(_logger, itemId);
            return null;
        }

        var segments = await _mediaSegmentManager
            .GetSegmentsAsync(item, null, MediaSegmentProviderDefaults.ExternalProviders, filterByProvider: false)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return segments.FirstOrDefault(segment => segment.Id == segmentId);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Item not found for episode {EpisodeId}")]
    private static partial void LogItemNotFound(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intro Skipper provider entry not found for item {ItemId}; Jellyfin segment will not be created")]
    private static partial void LogProviderNotFound(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error deleting segment {SegmentId}")]
    private static partial void LogErrorDeletingSegment(ILogger logger, Exception ex, Guid segmentId);
}
