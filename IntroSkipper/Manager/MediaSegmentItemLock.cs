// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Coordinates in-process mutations of one item's media segments.
/// </summary>
/// <remarks>
/// The same semaphore is returned for every caller of a given item ID, so callers must
/// acquire it before reading or writing either segment store and must dispose the returned
/// lease, normally with <c>using</c>. Entries are reference counted and dropped
/// as soon as the last holder releases them: a full-library refresh touches every item, so
/// retaining one semaphore per item would grow without bound for the plugin's lifetime.
/// </remarks>
internal static class MediaSegmentItemLock
{
    private static readonly Dictionary<Guid, Entry> ItemLocks = [];

    /// <summary>
    /// Determines whether an item currently has a live lock entry.
    /// </summary>
    /// <remarks>Exposed for tests that assert entries do not accumulate.</remarks>
    /// <param name="itemId">The ID of the item to probe.</param>
    /// <returns><see langword="true"/> when an entry is held or awaited.</returns>
    internal static bool IsTracked(Guid itemId)
    {
        lock (ItemLocks)
        {
            return ItemLocks.ContainsKey(itemId);
        }
    }

    /// <summary>
    /// Waits for exclusive access to an item's segments.
    /// </summary>
    /// <example>
    /// <code language="csharp">
    /// using var itemLock = await MediaSegmentItemLock.AcquireAsync(itemId, cancellationToken);
    /// await MutateSegmentsAsync(cancellationToken);
    /// </code>
    /// </example>
    /// <param name="itemId">The ID of the item whose mutations are coordinated.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns>A lease that releases the lock when disposed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled while waiting.</exception>
    public static async Task<IDisposable> AcquireAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var entry = Rent(itemId);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A canceled wait never entered the semaphore, so drop the reference
            // without releasing it; over-releasing would let two writers in at once.
            Return(itemId, entry);
            throw;
        }

        return new Lease(itemId, entry);
    }

    private static Entry Rent(Guid itemId)
    {
        lock (ItemLocks)
        {
            if (!ItemLocks.TryGetValue(itemId, out var entry))
            {
                entry = new Entry();
                ItemLocks[itemId] = entry;
            }

            entry.Holders++;
            return entry;
        }
    }

    private static void Return(Guid itemId, Entry entry)
    {
        lock (ItemLocks)
        {
            if (--entry.Holders > 0)
            {
                return;
            }

            // No one else holds or waits on this entry, and Rent cannot hand it out
            // again once it leaves the dictionary under this lock, so the semaphore
            // has no remaining observers.
            if (ItemLocks.TryGetValue(itemId, out var current) && ReferenceEquals(current, entry))
            {
                ItemLocks.Remove(itemId);
                entry.Semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// A single acquisition of an item's lock, released on dispose.
    /// </summary>
    private sealed class Lease(Guid itemId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                return;
            }

            // Release before returning the reference: Return may dispose the semaphore
            // once this was the last holder.
            entry.Semaphore.Release();
            Return(itemId, entry);
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>
        /// Gets or sets the number of callers holding or waiting for <see cref="Semaphore"/>.
        /// </summary>
        /// <remarks>Only mutated under the <see cref="ItemLocks"/> lock.</remarks>
        public int Holders { get; set; }
    }
}
