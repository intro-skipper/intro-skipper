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
/// Analyze all television episodes for media segments.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DetectSegmentsTask"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="analyzerTaskFactory">Analyzer task factory.</param>
public partial class DetectSegmentsTask(
    ILogger<DetectSegmentsTask> logger,
    BaseItemAnalyzerTaskFactory analyzerTaskFactory) : IScheduledTask
{
    private readonly ILogger<DetectSegmentsTask> _logger = logger;
    private readonly BaseItemAnalyzerTaskFactory _analyzerTaskFactory = analyzerTaskFactory;

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
    /// Analyze all episodes in the queue. Only one instance of this task should be run at a time.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // abort automatic analyzer if running
        if (Entrypoint.AutomaticTaskState == TaskState.Running || Entrypoint.AutomaticTaskState == TaskState.Cancelling)
        {
            LogAutomaticTaskWillBeCanceled(_logger, Entrypoint.AutomaticTaskState);
            await Entrypoint.CancelAutomaticTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        using (await ScheduledTaskSemaphore.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            LogScheduledTaskStarting(_logger);

            var baseIntroAnalyzer = _analyzerTaskFactory.Create(_logger);

            await baseIntroAnalyzer.AnalyzeItemsAsync(progress, cancellationToken).ConfigureAwait(false);
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic Task is {TaskState} and will be canceled.")]
    private static partial void LogAutomaticTaskWillBeCanceled(ILogger logger, TaskState taskState);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled Task is starting")]
    private static partial void LogScheduledTaskStarting(ILogger logger);
}
