// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace IntroSkipper.ScheduledTasks;

internal sealed class ScheduledTaskSemaphore : IDisposable
{
    // Application-lifetime singleton; intentionally never disposed.
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static int _activeHolders;
    private int _disposed;

    private ScheduledTaskSemaphore()
    {
        Interlocked.Increment(ref _activeHolders);
    }

    public static bool IsBusy => Volatile.Read(ref _activeHolders) > 0;

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ScheduledTaskSemaphore();
    }

    public static async Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ScheduledTaskSemaphore();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Decrement(ref _activeHolders);
        _semaphore.Release();
    }
}
