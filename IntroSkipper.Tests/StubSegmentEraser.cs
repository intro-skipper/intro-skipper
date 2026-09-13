// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.SegmentChanges;

/// <summary>
/// Counting <see cref="ISegmentEraser"/> whose hook runs before every erase, which then
/// goes through the inner eraser when one is given and is otherwise a no-op. A hook that
/// throws or parks makes the erase fail or hang.
/// </summary>
internal sealed class StubSegmentEraser(ISegmentEraser? inner = null) : ISegmentEraser
{
    private int _erases;

    public int Erases => _erases;

    public Func<CancellationToken, Task> OnErase { get; set; } = _ => Task.CompletedTask;

    public async Task<(int RemovedSegments, int RemovedCacheEntries)> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, bool eraseCache, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _erases);
        await OnErase(cancellationToken);
        return inner is null ? (0, 0) : await inner.EraseItemsAsync(itemIds, eraseCache, cancellationToken);
    }
}
