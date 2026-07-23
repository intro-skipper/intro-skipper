// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace IntroSkipper.Manager;

/// <summary>
/// Coordinates in-process mutations of one item's media segments.
/// </summary>
/// <remarks>
/// The same semaphore is returned for every caller of a given item ID, so callers must
/// await it before reading or writing either segment store and must release it in a
/// <c>finally</c> block. Creating a lock has a process-lifetime side effect: entries are
/// retained for the lifetime of the plugin.
/// </remarks>
internal static class MediaSegmentItemLock
{
    // Entries intentionally remain process-lifetime scoped. Add eviction only if the
    // distinct item count becomes measurable in production.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ItemLocks = [];

    /// <summary>
    /// Gets the shared semaphore for an item.
    /// </summary>
    /// <remarks>
    /// This method does not wait. Callers must await <see cref="SemaphoreSlim.WaitAsync()"/>
    /// and release the returned semaphore exactly once after entering it.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// var itemLock = MediaSegmentItemLock.Get(itemId);
    /// await itemLock.WaitAsync(cancellationToken);
    /// try
    /// {
    ///     await MutateSegmentsAsync(cancellationToken);
    /// }
    /// finally
    /// {
    ///     itemLock.Release();
    /// }
    /// </code>
    /// </example>
    /// <param name="itemId">The ID of the item whose mutations are coordinated.</param>
    /// <returns>A process-wide semaphore for <paramref name="itemId"/>.</returns>
    public static SemaphoreSlim Get(Guid itemId)
        => ItemLocks.GetOrAdd(itemId, static _ => new SemaphoreSlim(1, 1));
}
