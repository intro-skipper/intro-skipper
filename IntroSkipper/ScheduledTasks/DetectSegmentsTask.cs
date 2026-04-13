// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using IntroSkipper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyze all television episodes for media segments.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DetectSegmentsTask"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="logger">Logger.</param>
/// <param name="mediaSegmentUpdateManager">Media segment update manager.</param>
/// <param name="taskManager">Task manager used to queue the Jellyfin segment extraction task after analysis.</param>
public partial class DetectSegmentsTask(
    ILogger<DetectSegmentsTask> logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    MediaSegmentUpdateManager mediaSegmentUpdateManager,
    ITaskManager taskManager) : IScheduledTask
{
    private const string JellyfinMediaSegmentExtractionTaskKey = "TaskExtractMediaSegments";

    private readonly ILogger<DetectSegmentsTask> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly ITaskManager _taskManager = taskManager;

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
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        // abort automatic analyzer if running
        if (Entrypoint.AutomaticTaskState == TaskState.Running || Entrypoint.AutomaticTaskState == TaskState.Cancelling)
        {
            LogAutomaticTaskWillBeCanceled(_logger, Entrypoint.AutomaticTaskState);
            await Entrypoint.CancelAutomaticTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        using (await ScheduledTaskSemaphore.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            LogScheduledTaskStarting(_logger);

            var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                _loggerFactory.CreateLogger<DetectSegmentsTask>(),
                _loggerFactory,
                _libraryManager,
                _providerManager,
                _fileSystem,
                _mediaSegmentUpdateManager);

            // Newly analyzed segments are pushed to Jellyfin immediately via UpdateMediaSegmentsAsync.
            // In addition, trigger Jellyfin's built-in segment extraction task so that any segments
            // already stored in the Intro Skipper database (from a prior run) are also reflected in
            // Jellyfin's own database without waiting for its next scheduled extraction.
            await baseIntroAnalyzer.AnalyzeItemsAsync(progress, cancellationToken).ConfigureAwait(false);
            var extractionTask = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, JellyfinMediaSegmentExtractionTaskKey, StringComparison.OrdinalIgnoreCase));

            if (extractionTask is null)
            {
                LogExtractionTaskNotFound(_logger, JellyfinMediaSegmentExtractionTaskKey);
            }
            else if (extractionTask.State != TaskState.Idle)
            {
                LogExtractionTaskNotIdle(_logger, extractionTask.State);
            }
            else
            {
                LogQueueingSegmentExtraction(_logger);
                _taskManager.QueueScheduledTask(extractionTask.ScheduledTask, new TaskOptions());
            }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Queuing Jellyfin media segment extraction task to pull updated segments from Intro Skipper database")]
    private static partial void LogQueueingSegmentExtraction(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Jellyfin segment extraction task with key '{TaskKey}' was not found; segments will not be pushed until Jellyfin runs its next scheduled extraction.")]
    private static partial void LogExtractionTaskNotFound(ILogger logger, string taskKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Jellyfin segment extraction task is currently in state '{TaskState}' and will not be queued; segments will be picked up on its next run.")]
    private static partial void LogExtractionTaskNotIdle(ILogger logger, TaskState taskState);
}
