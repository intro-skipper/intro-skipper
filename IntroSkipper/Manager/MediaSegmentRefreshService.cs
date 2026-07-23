using IntroSkipper.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Syncs Intro Skipper's segments for library items into Jellyfin's database by
/// replacing Intro Skipper-owned entries directly; other providers are never touched.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentRefreshService"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments.</param>
/// <param name="segmentDtoFactory">Converts plugin segments to Jellyfin DTOs.</param>
/// <param name="libraryManager">The Jellyfin library manager used to resolve items by id.</param>
/// <param name="logger">Application logger.</param>
public sealed partial class MediaSegmentRefreshService(
    IJellyfinSegmentStore segmentStore,
    SegmentDtoFactory segmentDtoFactory,
    ILibraryManager libraryManager,
    ILogger<MediaSegmentRefreshService> logger) : IMediaSegmentRefresher
{
    /// <inheritdoc />
    public async Task RefreshAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await RefreshCoreAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Where(static itemId => itemId != Guid.Empty).ToHashSet();

        if (ids.Count == 0)
        {
            return;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Plugin.Instance!.Configuration.MaxParallelism),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(ids, options, async (itemId, ct) =>
        {
            var item = libraryManager.GetItemById(itemId);
            if (item is null)
            {
                LogItemNotFoundForMediaSegmentOperation(logger, itemId);
                return;
            }

            await RefreshCoreAsync(item, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        // No library resolution here: rows of items already removed from the library
        // must be deleted too, which is exactly what the stale-cleanup caller needs.
        await segmentStore.DeleteOwnSegmentsAsync(itemIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshCoreAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            var segments = await segmentDtoFactory.CreateAsync(item.Id, cancellationToken).ConfigureAwait(false);
            await segmentStore.ReplaceSegmentsAsync(item.Id, segments, cancellationToken).ConfigureAwait(false);
            LogUpdatedMediaSegments(logger, item.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogMediaSegmentRefreshCanceled(logger, item.Id);
            throw;
        }
        catch (Exception ex)
        {
            // Do not swallow cancellation or critical exceptions.
            if (ex is OperationCanceledException
                or OutOfMemoryException
                or StackOverflowException
                or ThreadAbortException
                or ThreadInterruptedException
                or AccessViolationException)
            {
                throw;
            }

            LogErrorRefreshingMediaSegments(logger, ex, item.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated media segments for item {ItemId}")]
    private static partial void LogUpdatedMediaSegments(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Media segment refresh for item {ItemId} was canceled.")]
    private static partial void LogMediaSegmentRefreshCanceled(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error refreshing media segments for item {ItemId}")]
    private static partial void LogErrorRefreshingMediaSegments(ILogger logger, Exception ex, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Item not found for media segment operation {ItemId}")]
    private static partial void LogItemNotFoundForMediaSegmentOperation(ILogger logger, Guid itemId);
}
