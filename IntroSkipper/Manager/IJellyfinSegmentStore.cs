// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
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
    /// Creates the given segment unless an identical entry (same type, start and end ticks,
    /// any provider) already exists for the item. The if-absent guarantee holds across
    /// database connections: when a competing writer commits an identical entry
    /// concurrently, that entry wins and at most one row remains after the call returns.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="segment">The segment to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <exception cref="SegmentIdConflictException">The supplied segment id already identifies another row.</exception>
    Task CreateCommercialIfAbsentAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a segment by item and segment id, regardless of provider. The snapshot
    /// carries the owning provider id so callers can scope follow-up mutations to Intro
    /// Skipper-owned rows.
    /// </summary>
    /// <param name="itemId">The item id that owns the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment row, or <c>null</c> if not found.</returns>
    Task<JellyfinSegmentSnapshot?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns whether a segment id identifies a row anywhere in the table, regardless of
    /// item or provider. Callers use this to tell a re-minted id (gone everywhere) apart
    /// from an id that is alive under a different item, which must not be treated as stale.
    /// </summary>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when some row carries the id.</returns>
    Task<bool> SegmentIdExistsAsync(Guid segmentId, CancellationToken cancellationToken);

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

    /// <summary>
    /// Replaces every segment of the given types for an item with Intro Skipper-owned segments.
    /// </summary>
    /// <remarks>
    /// The replacement removes segments regardless of provider in one transaction. An
    /// empty <paramref name="segments"/> collection deletes all segments of
    /// <paramref name="types"/>. Segments of other types remain unchanged.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose segments are replaced.</param>
    /// <param name="segments">The segments that will exist after the operation.</param>
    /// <param name="types">The segment types whose existing rows are replaced.</param>
    /// <param name="cancellationToken">The token that cancels the asynchronous transaction.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentException">A segment type is outside <paramref name="types"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> or <paramref name="types"/> is <see langword="null"/>.</exception>
    /// <exception cref="SegmentIdConflictException">A supplied segment id already identifies a row outside the replaced scope.</exception>
    Task ReplaceEditableTypesAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, IReadOnlyCollection<MediaSegmentType> types, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all of an item's segments across every provider, ordered by start position,
    /// including each row's owning provider id.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The item's segment rows.</returns>
    Task<IReadOnlyList<JellyfinSegmentSnapshot>> GetItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns per-item segment row counts across the whole table, split into Intro
    /// Skipper-owned rows and rows owned by other providers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per distinct item id present in the table.</returns>
    Task<IReadOnlyList<ItemSegmentCounts>> GetItemSegmentCountsAsync(CancellationToken cancellationToken);
}
