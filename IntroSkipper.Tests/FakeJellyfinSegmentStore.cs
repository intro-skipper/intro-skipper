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
/// Emulates <see cref="IJellyfinSegmentStore"/> for deterministic tests.
/// </summary>
/// <remarks>
/// The fake records writes, supplies configured reads, and can block configured writes
/// after recording them. Gates and signals allow tests to establish a precise interleaving
/// without depending on timing.
/// </remarks>
internal sealed class FakeJellyfinSegmentStore : IJellyfinSegmentStore
{
    private int _writeCount;

    /// <summary>
    /// Gets the segments served by <see cref="GetSegmentAsync"/>, matched by item id and
    /// segment id exactly like the production store.
    /// </summary>
    public IReadOnlyList<MediaSegmentDto> ExistingSegments { get; init; } = [];

    public Exception? WriteException { get; init; }

    public Exception? DeleteSegmentException { get; init; }

    public Exception? DeleteOwnException { get; init; }

    /// <summary>
    /// Gets the optional gate awaited by writes for <see cref="BlockedItemId"/>.
    /// </summary>
    /// <remarks>
    /// When <see cref="GateOnlyFirstWrite"/> is <see langword="true"/>, only the first
    /// matching write awaits this gate.
    /// </remarks>
    public TaskCompletionSource? WriteGate { get; init; }

    /// <summary>
    /// Gets a value that indicates whether only the first matching write awaits
    /// <see cref="WriteGate"/>.
    /// </summary>
    /// <value>
    /// <see langword="true"/> if only the first matching write is blocked; otherwise,
    /// <see langword="false"/>.
    /// </value>
    public bool GateOnlyFirstWrite { get; init; }

    /// <summary>
    /// Gets a signal completed immediately before a write for <see cref="BlockedItemId"/>
    /// starts awaiting <see cref="WriteGate"/>. Create it with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>. An inline
    /// continuation would run before the caller returns from the store call, preventing
    /// awaits-the-write assertions from observing the completed write.
    /// </summary>
    public TaskCompletionSource? WriteEntered { get; init; }

    /// <summary>
    /// Gets an optional callback invoked after <see cref="ReplaceEditableTypesAsync"/>
    /// records a successful write.
    /// </summary>
    /// <remarks>
    /// The callback runs synchronously inside the fake store call and is intended for
    /// tests that cancel only after the Jellyfin replacement commits.
    /// </remarks>
    public Action? EditableTypesWriteCompleted { get; init; }

    /// <summary>
    /// Gets a signal completed when <see cref="DeleteSegmentAsync"/> begins.
    /// </summary>
    public TaskCompletionSource? DeleteEntered { get; init; }

    /// <summary>
    /// Gets the optional item ID whose writes are gated.
    /// </summary>
    public Guid? BlockedItemId { get; init; }

    public int WriteCallCount => _writeCount;

    /// <summary>
    /// Gets the snapshots served by <see cref="GetItemSegmentsAsync"/>, filtered by item id.
    /// </summary>
    public IReadOnlyList<JellyfinSegmentSnapshot> ItemSegments { get; init; } = [];

    /// <summary>
    /// Gets the counts served by <see cref="GetItemSegmentCountsAsync"/>.
    /// </summary>
    public IReadOnlyList<ItemSegmentCounts> SegmentCounts { get; init; } = [];

    public List<(Guid ItemId, IReadOnlyList<MediaSegmentDto> Segments)> ReplacedItems { get; } = [];

    public List<(Guid ItemId, IReadOnlyList<MediaSegmentDto> Segments, IReadOnlyList<MediaSegmentType> Types)> ReplacedEditableTypes { get; } = [];

    public List<(Guid ItemId, MediaSegmentDto Segment)> ReplacedTypes { get; } = [];

    public List<(Guid ItemId, MediaSegmentDto Segment)> CreatedCommercials { get; } = [];

    public List<(Guid ItemId, Guid SegmentId)> DeletedSegments { get; } = [];

    public List<Guid> DeletedOwnItemIds { get; } = [];

    public async Task ReplaceSegmentsAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, CancellationToken cancellationToken)
    {
        var writeNumber = Interlocked.Increment(ref _writeCount);
        lock (ReplacedItems)
        {
            ReplacedItems.Add((itemId, segments));
        }

        await WaitIfGatedAsync(itemId, writeNumber);
        ThrowIfConfigured(WriteException);
    }

    public async Task ReplaceTypeAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        var writeNumber = Interlocked.Increment(ref _writeCount);
        lock (ReplacedTypes)
        {
            ReplacedTypes.Add((itemId, segment));
        }

        await WaitIfGatedAsync(itemId, writeNumber);
        ThrowIfConfigured(WriteException);
    }

    public async Task CreateCommercialIfAbsentAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        var writeNumber = Interlocked.Increment(ref _writeCount);
        lock (CreatedCommercials)
        {
            CreatedCommercials.Add((itemId, segment));
        }

        await WaitIfGatedAsync(itemId, writeNumber);
        ThrowIfConfigured(WriteException);
    }

    public Task DeleteOwnSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteOwnException);
        DeletedOwnItemIds.AddRange(itemIds);
        return Task.CompletedTask;
    }

    public Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
        => Task.FromResult(ExistingSegments.FirstOrDefault(segment => segment.ItemId == itemId && segment.Id == segmentId));

    public async Task ReplaceEditableTypesAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, IReadOnlyCollection<MediaSegmentType> types, CancellationToken cancellationToken)
    {
        var writeNumber = Interlocked.Increment(ref _writeCount);
        lock (ReplacedEditableTypes)
        {
            ReplacedEditableTypes.Add((itemId, segments, types.ToList()));
        }

        await WaitIfGatedAsync(itemId, writeNumber);
        ThrowIfConfigured(WriteException);
        EditableTypesWriteCompleted?.Invoke();
    }

    public Task<IReadOnlyList<JellyfinSegmentSnapshot>> GetItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<JellyfinSegmentSnapshot>>(
            ItemSegments.Where(snapshot => snapshot.ItemId == itemId).ToList());

    public Task<IReadOnlyList<ItemSegmentCounts>> GetItemSegmentCountsAsync(CancellationToken cancellationToken)
        => Task.FromResult(SegmentCounts);

    public Task DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured(DeleteSegmentException);
        DeleteEntered?.TrySetResult();
        DeletedSegments.Add((itemId, segmentId));
        return Task.CompletedTask;
    }

    private async Task WaitIfGatedAsync(Guid itemId, int writeNumber)
    {
        if (WriteGate is not null
            && itemId == BlockedItemId
            && (!GateOnlyFirstWrite || writeNumber == 1))
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
