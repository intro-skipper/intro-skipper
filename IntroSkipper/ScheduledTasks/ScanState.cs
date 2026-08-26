// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Tasks;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Single owner of the "is a scan running" definition, shared by the dashboard scan-status
/// endpoint and the support bundle so the two reports cannot drift apart. A scan is running
/// while any holder owns <see cref="ScheduledTaskSemaphore"/> (the scheduled detection task,
/// a manual season scan, or the automatic analysis) or while the detection task worker
/// reports activity - the worker's state briefly extends past the semaphore lease during
/// task startup and teardown, and stays Cancelling until the task observes the cancellation.
/// </summary>
internal static class ScanState
{
    public static IScheduledTaskWorker? FindDetectTask(ITaskManager taskManager)
        => taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is DetectSegmentsTask);

    public static bool IsRunning(IScheduledTaskWorker? detectTask)
        => ScheduledTaskSemaphore.IsBusy || detectTask?.State is TaskState.Running or TaskState.Cancelling;
}
