using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using ConfusedPolarBear.Plugin.IntroSkipper.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyze all television episodes for credits.
/// TODO: analyze all media files.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DetectCreditsTask"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="logger">Logger.</param>
/// <param name="mediaSegmentManager">MediaSegmentManager.</param>
public class DetectCreditsTask(
    ILogger<DetectCreditsTask> logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IMediaSegmentManager mediaSegmentManager) : IScheduledTask
{
    private readonly ILogger<DetectCreditsTask> _logger = logger;

    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    private readonly ILibraryManager _libraryManager = libraryManager;

    private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Detect Credits";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Analyzes media to determine the timestamp and length of credits";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperDetectCredits";

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

            var modes = new List<AnalysisMode> { AnalysisMode.Credits };

            var baseCreditAnalyzer = new BaseItemAnalyzerTask(
                modes,
                _loggerFactory.CreateLogger<DetectCreditsTask>(),
                _loggerFactory,
                _libraryManager,
                _mediaSegmentManager);

            await baseCreditAnalyzer.AnalyzeItems(progress, cancellationToken).ConfigureAwait(false);
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
