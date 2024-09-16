using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyze all television episodes for introduction sequences.
/// </summary>
public class DetectIntrosTask : IScheduledTask
{
    private readonly ILogger<DetectIntrosTask> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectIntrosTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public DetectIntrosTask(
        ILogger<DetectIntrosTask> logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
    }

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
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        // abort automatic analyzer if running
        if (Entrypoint.AutomaticTaskState == TaskState.Running || Entrypoint.AutomaticTaskState == TaskState.Cancelling)
        {
            _logger.LogInformation("Automatic Task is {0} and will be canceled.", Entrypoint.AutomaticTaskState);
            Entrypoint.CancelAutomaticTask(cancellationToken);
        }

        using (ScheduledTaskSemaphore.Acquire(-1, cancellationToken))
        {
            _logger.LogInformation("Scheduled Task is starting");

            var modes = new List<AnalysisMode> { AnalysisMode.Introduction };

            var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                modes.AsReadOnly(),
                _loggerFactory.CreateLogger<DetectIntrosTask>(),
                _loggerFactory,
                _libraryManager);

            baseIntroAnalyzer.AnalyzeItems(progress, cancellationToken);

            return Task.CompletedTask;
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
