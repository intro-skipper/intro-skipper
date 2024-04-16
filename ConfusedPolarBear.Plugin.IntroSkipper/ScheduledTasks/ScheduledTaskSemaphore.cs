using System;
using System.Threading;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

internal sealed class ScheduledTaskSemaphore : IDisposable
{
    private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    private ScheduledTaskSemaphore()
    {
    }

    public static bool Wait(int timeout, CancellationToken cancellationToken)
    {
        return _semaphore.Wait(timeout, cancellationToken);
    }

    public static int Release()
    {
        return _semaphore.Release();
    }

    public bool TryEnter()
    {
        return _semaphore.TryEnter();
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Protected dispose.
    /// </summary>
    /// <param name="disposing">Dispose.</param>
    private void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _semaphore.Dispose();
    }
}
