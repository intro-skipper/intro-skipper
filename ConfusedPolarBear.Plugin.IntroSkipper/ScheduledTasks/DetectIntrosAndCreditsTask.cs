using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Analyze all television episodes for introduction sequences.
/// </summary>
public class DetectIntrosAndCreditsTask : IScheduledTask
{
    private readonly ILoggerFactory _loggerFactory;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectIntrosAndCreditsTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    public DetectIntrosAndCreditsTask(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Detect Introductions and Credits";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Analyzes the audio of all television episodes to find introduction and credit sequences.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperDetectIntrosAndCredits";

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

        // abort if analyzer is already running
        if (Plugin.Instance!.AnalyzerTaskIsRunning)
        {
            return Task.CompletedTask;
        }
        else
        {
            Plugin.Instance!.AnalyzerTaskIsRunning = true;
        }

        if (Plugin.Instance!.Configuration.DetectIntros)
        {
            var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                AnalysisMode.Introduction,
                _loggerFactory.CreateLogger<DetectIntrosAndCreditsTask>(),
                _loggerFactory,
                _libraryManager);

            baseIntroAnalyzer.AnalyzeItems(progress, cancellationToken);
        }

        if (Plugin.Instance!.Configuration.DetectCredits)
        {
            var baseCreditAnalyzer = new BaseItemAnalyzerTask(
                AnalysisMode.Credits,
                _loggerFactory.CreateLogger<DetectIntrosAndCreditsTask>(),
                _loggerFactory,
                _libraryManager);

            baseCreditAnalyzer.AnalyzeItems(progress, cancellationToken);
        }

        Plugin.Instance!.AnalyzerTaskIsRunning = false;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks
            }
        };
    }
}
