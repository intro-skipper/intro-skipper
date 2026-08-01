using MediaBrowser.Controller.Entities;

namespace IntroSkipper.Manager;

/// <summary>
/// Refreshes Jellyfin media segments for library items.
/// </summary>
public interface IMediaSegmentRefresher
{
    /// <summary>
    /// Refreshes media segments for an item.
    /// </summary>
    /// <param name="item">The item to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RefreshAsync(BaseItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes media segments for an item, propagating failures instead of the
    /// log-and-continue behavior of <see cref="RefreshAsync(BaseItem, CancellationToken)"/>.
    /// For interactive mutations whose response must not report success while the
    /// Jellyfin mirror still holds the old rows.
    /// </summary>
    /// <param name="item">The item to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RefreshStrictAsync(BaseItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes media segments for item ids.
    /// </summary>
    /// <param name="itemIds">The item ids to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes Intro Skipper-owned media segments by refreshing items with only other providers.
    /// </summary>
    /// <param name="itemIds">The item ids whose Intro Skipper segments should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default);
}
