// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;
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
    /// Reads all of Intro Skipper's segment rows for an item; other providers' rows are
    /// never returned. Lets sync callers compare the mirrored state against an intended
    /// push and skip the write when nothing changed.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The item's Intro Skipper segment rows; empty when none exist.</returns>
    Task<IReadOnlyList<MediaSegmentDto>> GetOwnSegmentsAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a segment by id alone — any item, any provider. The resolution read
    /// for external deletes, which must tell a missing row apart from one owned by
    /// another item before any validation can happen.
    /// </summary>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment, or <c>null</c> if not found.</returns>
    Task<MediaSegmentDto?> FindSegmentAsync(Guid segmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a segment only while it still matches its validated shape — item, id,
    /// type and boundaries travel in one delete predicate — so no concurrent rewrite
    /// of the row under its stable id can slip between a check and the delete.
    /// </summary>
    /// <param name="itemId">The item id that must own the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="type">The type the row carried when the delete was validated.</param>
    /// <param name="startTicks">The start ticks the row carried when the delete was validated.</param>
    /// <param name="endTicks">The end ticks the row carried when the delete was validated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted: 0 when no row matched the full predicate
    /// (vanished, or changed since validation).</returns>
    Task<int> DeleteValidatedSegmentAsync(Guid itemId, Guid segmentId, MediaSegmentType type, long startTicks, long endTicks, CancellationToken cancellationToken);
}
