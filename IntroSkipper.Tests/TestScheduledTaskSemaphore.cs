// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading.Tasks;

namespace IntroSkipper.Tests;

using IntroSkipper.ScheduledTasks;
using Xunit;

[Collection("ScheduledTaskSemaphore")]
public class TestScheduledTaskSemaphore
{
    [Fact]
    public async Task TryAcquireAsync_ReturnsBusyUntilReleased()
    {
        Assert.False(ScheduledTaskSemaphore.IsBusy);
        var lease = await ScheduledTaskSemaphore.TryAcquireAsync();
        Assert.NotNull(lease);
        try
        {
            Assert.True(ScheduledTaskSemaphore.IsBusy);
            Assert.Null(await ScheduledTaskSemaphore.TryAcquireAsync());
        }
        finally
        {
            lease.Dispose();
        }

        Assert.False(ScheduledTaskSemaphore.IsBusy);
    }
}
