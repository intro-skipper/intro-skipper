// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.ScheduledTasks;

internal static class ScheduledTaskSemaphore
{
    // Application-lifetime singleton; intentionally never disposed.
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    public static bool IsBusy => _semaphore.CurrentCount == 0;

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease();
    }

    public static IDisposable? TryAcquire()
        => _semaphore.Wait(0) ? new Lease() : null;

    private static void ReleaseSemaphore()
    {
        _semaphore.Release();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                return;
            }

            ReleaseSemaphore();
        }
    }
}
