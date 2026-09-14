// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyzes every season of every enabled library through the analysis queue.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DetectSegmentsTask"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="queue">Analysis queue that runs the library pass.</param>
public partial class DetectSegmentsTask(
    ILogger<DetectSegmentsTask> logger,
    AnalysisScheduler queue) : IScheduledTask
{
    private readonly ILogger<DetectSegmentsTask> _logger = logger;
    private readonly AnalysisScheduler _queue = queue;

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Detect and Analyze Media Segments";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Analyzes media to determine the timestamp and length of intros and credits.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "IntroSkipperDetectSegmentsTask";

    /// <summary>
    /// Requests a library pass and waits for it. The pass starts once any pass in flight
    /// and any pending manual scan have finished. Cancelling this task withdraws the
    /// request while it waits, or cancels only the library pass once it runs and returns
    /// when the pass has stopped.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogScheduledTaskStarting(_logger);
        return _queue.RunLibraryAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks
            }
        ];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled Task is starting")]
    private static partial void LogScheduledTaskStarting(ILogger logger);
}
