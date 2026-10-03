// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;

/// <summary>
/// Hand-rolled <see cref="IJellyfinSegmentStore"/> fake over live rows: Intro Skipper's
/// own rows (seeded from <see cref="ExistingSegments"/>, replaced by successful writes)
/// and other providers' rows (<see cref="ForeignSegments"/>, never touched by a
/// replace). Lookups and validated deletes act on both, like Jellyfin's table. It
/// records calls, and optionally throws or parks the first write for
/// <see cref="BlockedItemId"/> on <see cref="WriteGate"/> after recording it. Releasing
/// the gate with SetException fails exactly that write; later writes succeed.
/// </summary>
internal sealed class FakeJellyfinSegmentStore : IJellyfinSegmentStore
{
    private readonly object _mirrorLock = new();
    private int _writeCount;
    private int _gatedWriteParked;
    private Dictionary<Guid, List<MediaSegmentDto>>? _mirrorRows;

    /// <summary>
    /// Gets the seed of Intro Skipper's own rows, the state
    /// <see cref="GetOwnSegmentsAsync"/> serves until a write replaces an item's rows.
    /// </summary>
    public IReadOnlyList<MediaSegmentDto> ExistingSegments { get; init; } = [];

    /// <summary>
    /// Gets other providers' rows, keyed by segment id. Tests may add, rewrite or remove
    /// rows between calls; a replace never touches them.
    /// </summary>
    public Dictionary<Guid, MediaSegmentDto> ForeignSegments { get; } = [];

    /// <summary>Gets or sets the exception every write throws after recording it.</summary>
    public Exception? WriteException { get; set; }

    /// <summary>Gets or sets the exception every validated delete throws before deleting.</summary>
    public Exception? DeleteSegmentException { get; set; }

    /// <summary>Gets or sets the exception every lookup by id throws.</summary>
    public Exception? FindSegmentException { get; set; }

    public TaskCompletionSource? WriteGate { get; init; }

    /// <summary>
    /// Gets a signal completed immediately before a write for <see cref="BlockedItemId"/>
    /// starts awaiting <see cref="WriteGate"/>. Create it with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>: an inline
    /// continuation would run while the caller is still inside the store call, before it
    /// could possibly have completed, blinding awaits-the-write assertions.
    /// </summary>
    public TaskCompletionSource? WriteEntered { get; init; }

    public Guid? BlockedItemId { get; init; }

    public int WriteCallCount => _writeCount;

    public List<(Guid ItemId, IReadOnlyList<MediaSegmentDto> Segments)> ReplacedItems { get; } = [];

    public List<(Guid ItemId, Guid SegmentId)> DeletedSegments { get; } = [];

    public async Task ReplaceSegmentsAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCount);
        lock (ReplacedItems)
        {
            ReplacedItems.Add((itemId, segments));
        }

        await WaitIfGatedAsync(itemId);
        ThrowIfConfigured(WriteException);

        // Only a successful write commits, like the production store's transaction.
        lock (_mirrorLock)
        {
            MirrorRows[itemId] = [.. segments];
        }
    }

    public Task<IReadOnlyList<MediaSegmentDto>> GetOwnSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        lock (_mirrorLock)
        {
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(
                MirrorRows.TryGetValue(itemId, out var rows) ? [.. rows] : []);
        }
    }

    public Task<MediaSegmentDto?> FindSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(FindSegmentException);
        lock (_mirrorLock)
        {
            return Task.FromResult(MirrorRows.Values.SelectMany(rows => rows).FirstOrDefault(segment => segment.Id == segmentId)
                ?? ForeignSegments.GetValueOrDefault(segmentId));
        }
    }

    public Task<int> DeleteValidatedSegmentAsync(Guid itemId, Guid segmentId, MediaSegmentType type, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteSegmentException);
        bool Matches(MediaSegmentDto segment) => segment.ItemId == itemId
            && segment.Id == segmentId
            && segment.Type == type
            && segment.StartTicks == startTicks
            && segment.EndTicks == endTicks;

        lock (_mirrorLock)
        {
            var deleted = (MirrorRows.TryGetValue(itemId, out var rows) ? rows.RemoveAll(Matches) : 0)
                + (ForeignSegments.TryGetValue(segmentId, out var foreign) && Matches(foreign) && ForeignSegments.Remove(segmentId) ? 1 : 0);
            if (deleted > 0)
            {
                DeletedSegments.Add((itemId, segmentId));
            }

            return Task.FromResult(deleted);
        }
    }

    // Lazy so the init-only seed is complete before the first grouping; access only
    // under _mirrorLock.
    private Dictionary<Guid, List<MediaSegmentDto>> MirrorRows =>
        _mirrorRows ??= ExistingSegments
            .GroupBy(segment => segment.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

    private async Task WaitIfGatedAsync(Guid itemId)
    {
        if (WriteGate is not null && itemId == BlockedItemId
            && Interlocked.Exchange(ref _gatedWriteParked, 1) == 0)
        {
            WriteEntered?.TrySetResult();
            await WriteGate.Task;
        }
    }

    private static void ThrowIfConfigured(Exception? exception)
    {
        if (exception is not null)
        {
            throw exception;
        }
    }
}
