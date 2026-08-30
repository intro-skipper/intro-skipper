namespace IntroSkipper.Manager;

/// <summary>
/// Refreshes Jellyfin media segments for library items.
/// </summary>
public interface IMediaSegmentRefresher
{
    /// <summary>
    /// Converges the Jellyfin mirror of each item that is still in the library; ids that
    /// no longer resolve are skipped. A single item's mirror failure is logged and does
    /// not stop the others.
    /// </summary>
    /// <param name="itemIds">The item ids to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes Intro Skipper-owned media segments for the item ids directly, including
    /// items no longer in the library; other providers' segments are untouched.
    /// </summary>
    /// <param name="itemIds">The item ids whose Intro Skipper segments should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default);
}
