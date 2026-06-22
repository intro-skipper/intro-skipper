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
    /// Refreshes media segments for item ids.
    /// </summary>
    /// <param name="itemIds">The item ids to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default);
}
