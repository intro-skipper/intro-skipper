using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Analyze all television episodes for credits.
/// TODO: analyze all media files.
/// </summary>
public class DetectCreditsTask : IScheduledTask
{
    private readonly ILoggerFactory _loggerFactory;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectCreditsTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    public DetectCreditsTask(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
    }

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
    public string Description => "Analyzes the audio and video of all television episodes to find credits.";

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
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        // Wait for running analyzer
        if (Plugin.Instance!.AnalyzerTaskIsRunning)
        {
            try
            {
                while (Plugin.Instance!.AnalyzerTaskIsRunning)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Task.Delay(10000).Wait(cancellationToken); // Adjust delay as needed
                }
            }
            catch (OperationCanceledException)
            {
                 return Task.CompletedTask;
            }
        }

        Plugin.Instance!.AnalyzerTaskIsRunning = true;

        var baseAnalyzer = new BaseItemAnalyzerTask(
            AnalysisMode.Credits,
            _loggerFactory.CreateLogger<DetectCreditsTask>(),
            _loggerFactory,
            _libraryManager);

        baseAnalyzer.AnalyzeItems(progress, cancellationToken);

        Plugin.Instance!.AnalyzerTaskIsRunning = false;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
