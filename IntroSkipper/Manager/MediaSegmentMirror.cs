// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Providers;

namespace IntroSkipper.Manager;

/// <summary>
/// The single locked write path that mirrors the plugin database into Jellyfin's media
/// segments for one item: every active plugin segment is pushed (carrying its plugin row
/// id) and Intro Skipper rows no longer present in the plugin database are removed;
/// other providers' segments are never touched. The lock spans the plugin-database read
/// and the Jellyfin replace as one unit, so a write derived from a stale read can never
/// land after a newer one. No-op when mirroring is disabled
/// (<see cref="MediaSegmentMirrorPolicy"/>).
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentMirror"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments.</param>
/// <param name="segmentDtoFactory">Factory that converts stored plugin segments to Jellyfin DTOs.</param>
public sealed class MediaSegmentMirror(IJellyfinSegmentStore segmentStore, SegmentDtoFactory segmentDtoFactory)
{
    // A fixed stripe pool instead of per-item semaphores: allocation-free, bounded no
    // matter how many items a bulk refresh touches, and no eviction scheme to get wrong.
    // A stripe collision merely serializes two unrelated items' writes, which is harmless.
    private const int StripeCount = 32; // power of two so the index is a mask

    private readonly SemaphoreSlim[] _stripes = CreateStripes();

    /// <summary>
    /// Mirrors the plugin database into Jellyfin's media segments for one item. Writes
    /// for the same item are always serialized; distinct items are serialized only when
    /// their ids share a lock stripe.
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

        var stripe = _stripes[StripeIndex(itemId)];
        await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var segments = await segmentDtoFactory.CreateAsync(itemId, cancellationToken).ConfigureAwait(false);
            await segmentStore.ReplaceSegmentsAsync(itemId, segments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stripe.Release();
        }
    }

    /// <summary>
    /// Maps an item id to its lock stripe. Internal so concurrency tests can pick ids on
    /// distinct (or identical) stripes deterministically instead of flaking on hash luck.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The stripe index.</returns>
    internal static int StripeIndex(Guid itemId) => (int)((uint)itemId.GetHashCode() & (StripeCount - 1));

    private static SemaphoreSlim[] CreateStripes()
    {
        var stripes = new SemaphoreSlim[StripeCount];
        for (var i = 0; i < stripes.Length; i++)
        {
            stripes[i] = new SemaphoreSlim(1, 1);
        }

        return stripes;
    }
}
