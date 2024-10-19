using System;
using System.Threading;
using System.Threading.Tasks;

namespace ConfusedPolarBear.Plugin.IntroSkipper.ScheduledTasks;

internal sealed class ScheduledTaskSemaphore : IDisposable
{
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private ScheduledTaskSemaphore()
    {
    }

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ScheduledTaskSemaphore();
    }

    public void Dispose()
    {
        _semaphore.Release();
    }
}
