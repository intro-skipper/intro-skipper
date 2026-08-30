// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Helper;

namespace IntroSkipper.Manager;

/// <summary>
/// The per-item mutation stripes that serialize every interactive segment mutation,
/// shared by <see cref="MediaSegmentEditorService"/> and the durable segment-change
/// coordinator so the one-serializer-per-item invariant holds across both entry points
/// while they coexist. Separate pool from <see cref="MediaSegmentMirror"/>'s; lock
/// order is always mutation stripe -&gt; mirror stripe. Must be registered as a
/// singleton so all requests share the stripes.
/// </summary>
public sealed class SegmentMutationLocks
{
    private readonly StripedAsyncLock _locks = new();

    /// <summary>
    /// Acquires the item's mutation stripe, waiting until it is free. Dispose the
    /// returned releaser (a <c>using</c> declaration) to free the stripe.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token; only the wait is cancelable.</param>
    /// <returns>A releaser that frees the stripe on dispose.</returns>
    public async Task<IDisposable> AcquireAsync(Guid itemId, CancellationToken cancellationToken)
        => await _locks.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
}
