// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Helper;

/// <summary>
/// A fixed pool of asynchronous locks addressed by item id. A fixed stripe pool instead
/// of per-item semaphores: allocation-free, bounded no matter how many items a caller
/// touches, and no eviction scheme to get wrong. A stripe collision merely serializes
/// two unrelated items' operations, which is harmless. Stripes are never disposed, so an
/// instance must live for the process lifetime inside a singleton.
/// </summary>
internal class StripedAsyncLock
{
    private const int StripeCount = 32; // power of two so the index is a mask

    private readonly SemaphoreSlim[] _stripes = [.. Enumerable.Range(0, StripeCount).Select(static _ => new SemaphoreSlim(1, 1))];

    /// <summary>
    /// Acquires the item's lock stripe, waiting until it is free. Dispose the returned
    /// releaser (a <c>using</c> declaration) to release the stripe. The returned value
    /// must be awaited exactly once, directly.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token; only the wait is cancelable.</param>
    /// <returns>A releaser that frees the stripe on dispose.</returns>
    public async ValueTask<Releaser> AcquireAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var stripe = _stripes[StripeIndex(itemId)];
        await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(stripe);
    }

    /// <summary>
    /// Maps an item id to its lock stripe. The mapping is shared by every pool, and it
    /// is internal so concurrency tests can pick ids on distinct (or identical)
    /// stripes deterministically instead of flaking on hash luck.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The stripe index.</returns>
    internal static int StripeIndex(Guid itemId) => (int)((uint)itemId.GetHashCode() & (StripeCount - 1));

    /// <summary>
    /// Releases the stripe acquired by <see cref="AcquireAsync"/> when disposed. Dispose
    /// exactly once, where the lock was acquired — a <c>using</c> declaration does both.
    /// </summary>
    /// <param name="stripe">The acquired stripe.</param>
    public readonly struct Releaser(SemaphoreSlim stripe) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => stripe.Release();
    }
}
