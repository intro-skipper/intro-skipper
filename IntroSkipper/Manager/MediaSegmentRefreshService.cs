using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Syncs Intro Skipper's segments for library items into Jellyfin's database via the
/// shared <see cref="MediaSegmentMirror"/>; other providers are never touched.
/// All operations honor <see cref="MediaSegmentMirrorPolicy"/>: when mirroring is
/// disabled they are no-ops, so callers never gate them.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentRefreshService"/> class.
/// </remarks>
/// <param name="mirror">The shared locked mirror write path.</param>
/// <param name="libraryManager">The Jellyfin library manager used to resolve items by id.</param>
/// <param name="logger">Application logger.</param>
public sealed partial class MediaSegmentRefreshService(
    MediaSegmentMirror mirror,
    ILibraryManager libraryManager,
    ILogger<MediaSegmentRefreshService> logger) : IMediaSegmentRefresher
{
    /// <inheritdoc />
    public async Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        // The mirror gates its own writes; this early return only skips resolving every
        // id against the library for syncs that would all no-op.
        if (!MediaSegmentMirrorPolicy.Enabled)
        {
            return;
        }

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
            if (libraryManager.GetItemById(itemId) is null)
            {
                LogItemNotFoundForMediaSegmentOperation(logger, itemId);
                return;
            }

            await RefreshCoreAsync(itemId, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        // No library resolution here: rows of items already removed from the library
        // must be deleted too, which is exactly what the stale-cleanup caller needs.
        await mirror.DeleteOwnSegmentsAsync(itemIds, cancellationToken).ConfigureAwait(false);
    }

    // The lenient path: editor mutations converge the mirror through MediaSegmentMirror
    // directly and see its failures; here a non-critical failure is logged and swallowed.
    private async Task RefreshCoreAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
            LogUpdatedMediaSegments(logger, itemId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogMediaSegmentRefreshCanceled(logger, itemId);
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

            LogErrorRefreshingMediaSegments(logger, ex, itemId);
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
