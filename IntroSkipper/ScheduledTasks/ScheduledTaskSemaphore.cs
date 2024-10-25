// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Threading;

namespace IntroSkipper.ScheduledTasks;

internal sealed class ScheduledTaskSemaphore : IDisposable
{
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private ScheduledTaskSemaphore()
    {
    }

    public static IDisposable Acquire(CancellationToken cancellationToken)
    {
        _semaphore.Wait(cancellationToken);
        return new ScheduledTaskSemaphore();
    }

    public void Dispose()
    {
        _semaphore.Release();
    }
}
