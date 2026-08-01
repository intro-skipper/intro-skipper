// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Helper;
using IntroSkipper.Providers;

namespace IntroSkipper.Manager;

/// <summary>
/// The plugin's write path into Jellyfin's media segments: per-item locked convergence
/// (<see cref="SyncItemAsync"/>), targeted delete (<see cref="DeleteSegmentAsync"/>),
/// and a stripe-serialized bulk cleanup (<see cref="DeleteOwnSegmentsAsync"/>).
/// Other providers' segments are never touched, and every operation no-ops when
/// mirroring is disabled (<see cref="MediaSegmentMirrorPolicy"/>), so callers never
/// gate it. The one writer that bypasses this class is Jellyfin itself: it persists
/// <see cref="SegmentProvider"/> results during its own provider runs and can therefore
/// re-add a just-deleted segment from a read that predates the delete, until a later
/// sync converges the item.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentMirror"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments.</param>
/// <param name="segmentDtoFactory">Factory that converts stored plugin segments to Jellyfin DTOs.</param>
public sealed class MediaSegmentMirror(IJellyfinSegmentStore segmentStore, SegmentDtoFactory segmentDtoFactory)
{
    // Separate pool from MediaSegmentEditorService's mutation stripes; see
    // StripedAsyncLock for the pooling rationale.
    private readonly StripedAsyncLock _lock = new();

    /// <summary>
    /// Mirrors the plugin database into Jellyfin's media segments for one item: every
    /// active plugin segment is pushed (carrying its plugin row id) and Intro Skipper
    /// rows no longer present in the plugin database are removed. The lock spans the
    /// plugin-database read and the Jellyfin replace as one unit, so a write derived
    /// from a stale read can never land after a newer one; distinct items are
    /// serialized only when their ids share a lock stripe.
    /// </summary>
    /// <param name="itemId">The id of the media item to synchronize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SyncItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!MediaSegmentMirrorPolicy.Enabled)
        {
            return;
        }

        using var stripe = await _lock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var segments = await segmentDtoFactory.CreateAsync(itemId, cancellationToken).ConfigureAwait(false);
        await segmentStore.ReplaceSegmentsAsync(itemId, segments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes one of the item's Jellyfin segment rows by id under the item's lock, so
    /// the targeted delete serializes with concurrent <see cref="SyncItemAsync"/> calls
    /// instead of racing a bulk replace derived from a stale plugin-database read.
    /// </summary>
    /// <param name="itemId">The item id that must own the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened to the row.</returns>
    public async Task<MirrorDeleteOutcome> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        if (!MediaSegmentMirrorPolicy.Enabled)
        {
            return MirrorDeleteOutcome.MirroringDisabled;
        }

        using var stripe = await _lock.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var rowsDeleted = await segmentStore.DeleteSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
        return rowsDeleted > 0 ? MirrorDeleteOutcome.Deleted : MirrorDeleteOutcome.RowNotFound;
    }

    /// <summary>
    /// Deletes every Intro Skipper segment row for the given item ids, including items
    /// no longer in the library. The ids are grouped by lock stripe and each group's
    /// delete runs while holding its stripe, so the cleanup serializes with every
    /// per-item mirror write: an in-flight <see cref="SyncItemAsync"/> holding a stale
    /// plugin-database read cannot land its replace after this cleanup and resurrect
    /// rows whose plugin source the caller is removing, and any sync that starts later
    /// re-reads the plugin database and converges on what the caller left there.
    /// </summary>
    /// <param name="itemIds">The item ids whose Intro Skipper segments should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteOwnSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        if (!MediaSegmentMirrorPolicy.Enabled)
        {
            return;
        }

        foreach (var stripeGroup in itemIds.GroupBy(StripedAsyncLock.StripeIndex))
        {
            using var stripe = await _lock.AcquireStripeAsync(stripeGroup.Key, cancellationToken).ConfigureAwait(false);
            await segmentStore.DeleteOwnSegmentsAsync(stripeGroup, cancellationToken).ConfigureAwait(false);
        }
    }
}
