// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyze all television episodes for introduction sequences.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DetectIntrosTask"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="logger">Logger.</param>
/// <param name="mediaSegmentUpdateManager">MediaSegment Update Manager.</param>
public class DetectIntrosTask(
    ILogger<DetectIntrosTask> logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    MediaSegmentUpdateManager mediaSegmentUpdateManager) : IScheduledTask
{
    private readonly ILogger<DetectIntrosTask> _logger = logger;

    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    private readonly ILibraryManager _libraryManager = libraryManager;

    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Detect Intros";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Analyzes media to determine the timestamp and length of intros.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperDetectIntroductions";

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
            _logger.LogInformation("Automatic Task is {0} and will be canceled.", Entrypoint.AutomaticTaskState);
            await Entrypoint.CancelAutomaticTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        using (await ScheduledTaskSemaphore.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Scheduled Task is starting");

            var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                _loggerFactory.CreateLogger<DetectIntrosTask>(),
                _loggerFactory,
                _libraryManager,
                _mediaSegmentUpdateManager);

            await baseIntroAnalyzer.AnalyzeItems(progress, cancellationToken, [AnalysisMode.Introduction]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }
}
