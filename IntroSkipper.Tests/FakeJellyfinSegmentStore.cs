// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using MediaBrowser.Model.MediaSegments;

/// <summary>
/// Hand-rolled <see cref="IJellyfinSegmentStore"/> fake: records calls, serves
/// <see cref="ExistingSegments"/> lookups, and optionally throws or parks write calls
/// for <see cref="BlockedItemId"/> on <see cref="WriteGate"/> after recording them.
/// </summary>
internal sealed class FakeJellyfinSegmentStore : IJellyfinSegmentStore
{
    private int _writeCount;

    /// <summary>
    /// Gets the segments served by <see cref="GetSegmentAsync"/>, matched by segment id
    /// only (editor tests build entries without an item id).
    /// </summary>
    public IReadOnlyList<MediaSegmentDto> ExistingSegments { get; init; } = [];

    public Exception? WriteException { get; init; }

    public Exception? DeleteSegmentException { get; init; }

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

    public int WriteCallCount => _writeCount;

    public List<(Guid ItemId, IReadOnlyList<MediaSegmentDto> Segments)> ReplacedItems { get; } = [];

    public List<(Guid ItemId, MediaSegmentDto Segment)> ReplacedTypes { get; } = [];

    public List<(Guid ItemId, MediaSegmentDto Segment)> CreatedCommercials { get; } = [];

    public List<Guid> DeletedSegmentIds { get; } = [];

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
    }

    public async Task ReplaceTypeAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCount);
        lock (ReplacedTypes)
        {
            ReplacedTypes.Add((itemId, segment));
        }

        await WaitIfGatedAsync(itemId);
        ThrowIfConfigured(WriteException);
    }

    public async Task CreateCommercialIfAbsentAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCount);
        lock (CreatedCommercials)
        {
            CreatedCommercials.Add((itemId, segment));
        }

        await WaitIfGatedAsync(itemId);
        ThrowIfConfigured(WriteException);
    }

    public Task DeleteOwnSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteOwnException);
        DeletedOwnItemIds.AddRange(itemIds);
        return Task.CompletedTask;
    }

    public Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
        => Task.FromResult(ExistingSegments.FirstOrDefault(segment => segment.Id == segmentId));

    public Task DeleteSegmentAsync(Guid segmentId)
    {
        ThrowIfConfigured(DeleteSegmentException);
        DeletedSegmentIds.Add(segmentId);
        return Task.CompletedTask;
    }

    private async Task WaitIfGatedAsync(Guid itemId)
    {
        if (WriteGate is not null && itemId == BlockedItemId)
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
