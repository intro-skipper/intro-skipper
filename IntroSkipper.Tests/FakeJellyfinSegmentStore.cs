// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
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
/// Hand-rolled <see cref="IJellyfinSegmentStore"/> fake: records calls, serves
/// <see cref="ExistingSegments"/> lookups, and optionally throws or parks the first
/// write for <see cref="BlockedItemId"/> on <see cref="WriteGate"/> after recording it.
/// Releasing the gate with SetException fails exactly that write; later writes succeed.
/// <see cref="GetOwnSegmentsAsync"/> serves live per-item mirror state — seeded from
/// <see cref="ExistingSegments"/>, updated by successful writes — so the mirror's
/// skip-when-unchanged comparison sees what a real store would.
/// </summary>
internal sealed class FakeJellyfinSegmentStore : IJellyfinSegmentStore
{
    private readonly object _mirrorLock = new();
    private int _writeCount;
    private int _gatedWriteParked;
    private Dictionary<Guid, List<MediaSegmentDto>>? _mirrorRows;

    /// <summary>
    /// Gets the segments served by <see cref="GetSegmentAsync"/>, matched by item id and
    /// segment id exactly like the production store. Also the seed of the live mirror
    /// state served by <see cref="GetOwnSegmentsAsync"/> (the fake treats every seeded
    /// row as Intro Skipper's own).
    /// </summary>
    public IReadOnlyList<MediaSegmentDto> ExistingSegments { get; init; } = [];

    public Exception? WriteException { get; init; }

    public Exception? DeleteSegmentException { get; init; }

    /// <summary>
    /// Gets a callback invoked after a successful <see cref="DeleteSegmentAsync"/> is
    /// recorded, e.g. to cancel a token at the exact point where the Jellyfin delete
    /// has already committed.
    /// </summary>
    public Action? DeleteSegmentCallback { get; init; }

    public Exception? DeleteOwnException { get; init; }

    public TaskCompletionSource? WriteGate { get; init; }

    /// <summary>
    /// Gets a signal completed immediately before a write for <see cref="BlockedItemId"/>
    /// starts awaiting <see cref="WriteGate"/>. Create it with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>: an inline
    /// continuation would run while the caller is still inside the store call — before it
    /// could possibly have completed — blinding awaits-the-write assertions.
    /// </summary>
    public TaskCompletionSource? WriteEntered { get; init; }

    public Guid? BlockedItemId { get; init; }

    /// <summary>
    /// Gets the segment ids <see cref="DeleteSegmentAsync"/> reports as not found
    /// (returning 0 deleted rows, the drift signal). Every other delete reports one
    /// deleted row, simulating a correlated Jellyfin row.
    /// </summary>
    public IReadOnlyList<Guid> MissingSegmentIds { get; init; } = [];

    public int WriteCallCount => _writeCount;

    public List<(Guid ItemId, IReadOnlyList<MediaSegmentDto> Segments)> ReplacedItems { get; } = [];

    public List<(Guid ItemId, Guid SegmentId)> DeletedSegments { get; } = [];

    public List<Guid> DeletedOwnItemIds { get; } = [];

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

    public Task DeleteOwnSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteOwnException);
        var ids = itemIds.ToList();
        DeletedOwnItemIds.AddRange(ids);
        lock (_mirrorLock)
        {
            foreach (var itemId in ids)
            {
                MirrorRows.Remove(itemId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaSegmentDto>> GetOwnSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        lock (_mirrorLock)
        {
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(
                MirrorRows.TryGetValue(itemId, out var rows) ? [.. rows] : []);
        }
    }

    public Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
        => Task.FromResult(ExistingSegments.FirstOrDefault(segment => segment.ItemId == itemId && segment.Id == segmentId));

    public Task<MediaSegmentDto?> FindSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
        => Task.FromResult(ExistingSegments.FirstOrDefault(segment => segment.Id == segmentId));

    public Task<int> DeleteValidatedSegmentAsync(Guid itemId, Guid segmentId, MediaSegmentType type, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteSegmentException);
        var match = ExistingSegments.FirstOrDefault(segment => segment.ItemId == itemId
            && segment.Id == segmentId
            && segment.Type == type
            && segment.StartTicks == startTicks
            && segment.EndTicks == endTicks);
        if (match is null)
        {
            return Task.FromResult(0);
        }

        DeletedSegments.Add((itemId, segmentId));
        lock (_mirrorLock)
        {
            if (MirrorRows.TryGetValue(itemId, out var rows))
            {
                rows.RemoveAll(segment => segment.Id == segmentId);
            }
        }

        return Task.FromResult(1);
    }

    public Task<int> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteSegmentException);
        DeletedSegments.Add((itemId, segmentId));
        lock (_mirrorLock)
        {
            if (MirrorRows.TryGetValue(itemId, out var rows))
            {
                rows.RemoveAll(segment => segment.Id == segmentId);
            }
        }

        DeleteSegmentCallback?.Invoke();
        return Task.FromResult(MissingSegmentIds.Contains(segmentId) ? 0 : 1);
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
