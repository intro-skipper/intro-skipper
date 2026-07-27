// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Manager;

/// <summary>
/// Reads and writes Intro Skipper's media segments directly in Jellyfin's database.
/// </summary>
public interface IJellyfinSegmentStore
{
    /// <summary>
    /// Atomically replaces all of Intro Skipper's segments for an item with the given set.
    /// Other providers' segments are never touched. An empty set deletes Intro Skipper's
    /// segments for the item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="segments">The segments that should exist for the item after the call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ReplaceSegmentsAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes Intro Skipper's segments for the given item ids, including items that no
    /// longer exist in the library. Other providers' segments are never touched.
    /// </summary>
    /// <param name="itemIds">The item ids whose Intro Skipper segments should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteOwnSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a segment by item and segment id, regardless of provider.
    /// </summary>
    /// <param name="itemId">The item id that owns the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment, or <c>null</c> if not found.</returns>
    Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a segment by id when it belongs to the given item, regardless of provider.
    /// A segment id owned by a different item is left untouched, and an unknown id is a
    /// no-op so callers can clean up plugin-side rows whose Jellyfin row is already gone.
    /// </summary>
    /// <param name="itemId">The item id that must own the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken);
}
