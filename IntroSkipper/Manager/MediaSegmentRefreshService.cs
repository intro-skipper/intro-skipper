using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Refreshes Jellyfin media segments by running media-segment providers directly.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentRefreshService"/> class.
/// </remarks>
/// <param name="mediaSegmentManager">The Jellyfin media segment manager.</param>
/// <param name="libraryManager">The Jellyfin library manager used to resolve items by id.</param>
/// <param name="logger">Application logger.</param>
public sealed partial class MediaSegmentRefreshService(
    IMediaSegmentManager mediaSegmentManager,
    ILibraryManager libraryManager,
    ILogger<MediaSegmentRefreshService> logger) : IMediaSegmentRefresher
{
    /// <inheritdoc />
    public async Task RefreshAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await RefreshCoreAsync(
                item,
                MediaSegmentProviderDefaults.ExternalProviders,
                suppressErrors: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RefreshCoreAsync(
        BaseItem item,
        LibraryOptions libraryOptions,
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            await mediaSegmentManager.RunSegmentPluginProviders(item, libraryOptions, true, cancellationToken).ConfigureAwait(false);
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
                || ex is OutOfMemoryException
                || ex is StackOverflowException
                || ex is ThreadAbortException
                || ex is ThreadInterruptedException
                || ex is AccessViolationException)
            {
                throw;
            }

            LogErrorRefreshingMediaSegments(logger, ex, item.Id);
            if (!suppressErrors)
            {
                throw;
            }
        }
    }

    private async Task RefreshByIdAsync(
        Guid itemId,
        LibraryOptions libraryOptions,
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        if (itemId == Guid.Empty)
        {
            return;
        }

        var item = libraryManager.GetItemById(itemId);
        if (item is null)
        {
            LogItemNotFoundForMediaSegmentRefresh(logger, itemId);
            return;
        }

        await RefreshCoreAsync(item, libraryOptions, suppressErrors, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        => RefreshByIdsAsync(
            itemIds,
            MediaSegmentProviderDefaults.ExternalProviders,
            suppressErrors: true,
            cancellationToken);

    /// <inheritdoc />
    public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        => RefreshByIdsAsync(
            itemIds,
            MediaSegmentProviderDefaults.ExternalProvidersWithoutIntroSkipper,
            suppressErrors: false,
            cancellationToken);

    private async Task RefreshByIdsAsync(
        IEnumerable<Guid> itemIds,
        LibraryOptions libraryOptions,
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Where(itemId => itemId != Guid.Empty).ToHashSet();

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
            await RefreshByIdAsync(itemId, libraryOptions, suppressErrors, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated media segments for item {ItemId}")]
    private static partial void LogUpdatedMediaSegments(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Media segment refresh for item {ItemId} was canceled.")]
    private static partial void LogMediaSegmentRefreshCanceled(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error refreshing media segments for item {ItemId}")]
    private static partial void LogErrorRefreshingMediaSegments(ILogger logger, Exception ex, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Item not found for media segment refresh {ItemId}")]
    private static partial void LogItemNotFoundForMediaSegmentRefresh(ILogger logger, Guid itemId);
}
