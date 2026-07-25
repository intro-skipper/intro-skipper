// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Xunit;

public class MediaSegmentItemLockTests
{
    [Fact]
    public async Task Acquire_DropsEntry_WhenLastHolderReleases()
    {
        var itemId = Guid.NewGuid();

        var itemLock = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);
        Assert.True(MediaSegmentItemLock.IsTracked(itemId));
        itemLock.Dispose();

        // A full-library refresh acquires one lock per item, so entries must not survive
        // their last holder.
        Assert.False(MediaSegmentItemLock.IsTracked(itemId));
    }

    [Fact]
    public async Task Acquire_SerializesHolders_AndKeepsEntryWhileContended()
    {
        var itemId = Guid.NewGuid();
        var first = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);

        var second = MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.True(MediaSegmentItemLock.IsTracked(itemId));

        first.Dispose();

        // The waiter must inherit the same entry rather than get a second semaphore.
        var handle = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(MediaSegmentItemLock.IsTracked(itemId));
        handle.Dispose();
        Assert.False(MediaSegmentItemLock.IsTracked(itemId));
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var itemId = Guid.NewGuid();
        var itemLock = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);

        itemLock.Dispose();
        itemLock.Dispose();

        // A second dispose must not over-release the semaphore, which would let two
        // writers into the next acquisition at once.
        Assert.False(MediaSegmentItemLock.IsTracked(itemId));
        var first = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);
        var second = MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Acquire_DropsEntry_WhenWaitIsCanceled()
    {
        var itemId = Guid.NewGuid();
        var holder = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var pending = MediaSegmentItemLock.AcquireAsync(itemId, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The canceled waiter never entered the semaphore, so releasing the holder must
        // still leave the lock free for the next caller.
        holder.Dispose();
        Assert.False(MediaSegmentItemLock.IsTracked(itemId));

        var next = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        next.Dispose();
    }
}
