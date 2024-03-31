using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Server entrypoint.
/// </summary>
public class Entrypoint : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly ITaskManager _taskManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Entrypoint> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="Entrypoint"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="userViewManager">User view manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public Entrypoint(
        IUserManager userManager,
        IUserViewManager userViewManager,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<Entrypoint> logger,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userViewManager = userViewManager;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Protected dispose.
    /// </summary>
    /// <param name="dispose">Dispose.</param>
    protected virtual void Dispose(bool dispose)
    {
        if (!dispose)
        {
            return;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance!.Configuration.AutomaticAnalysis)
        {
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemModified;
            _taskManager.TaskCompleted += OnLibraryRefresh;
        }

        FFmpegWrapper.Logger = _logger;

        try
        {
            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            var queueManager = new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager);
            queueManager?.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    // Disclose source for inspiration
    // Implementation based on the principles of jellyfin-plugin-media-analyzer:
    // https://github.com/endrl/jellyfin-plugin-media-analyzer

    /// <summary>
    /// Library item was added.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private async void OnItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        // Debouncing logic (adjust delay as needed)
        await Task.Delay(20000).ConfigureAwait(false); // Delay analysis for 20 seconds

        try
        {
            OnDemandAnalyze();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error analyzing: {Exception}", ex);
        }
    }

    /// <summary>
    /// Library item was modified.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private async void OnItemModified(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        // Debouncing logic (adjust delay as needed)
        await Task.Delay(20000).ConfigureAwait(false); // Delay analysis for 20 seconds

        try
        {
            OnDemandAnalyze();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error analyzing: {Exception}", ex);
        }
    }

    /// <summary>
    /// TaskManager task ended.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
    private async void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
    {
        var result = eventArgs.Result;

        if (result.Key != "RefreshLibrary")
        {
            return;
        }

        if (result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        // Debouncing logic (adjust delay as needed)
        await Task.Delay(20000).ConfigureAwait(false); // Delay analysis for 20 seconds

        try
        {
            OnDemandAnalyze();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error analyzing: {Exception}", ex);
        }
    }

    private void OnDemandAnalyze()
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        var progress = new Progress<double>();
        var cancellationToken = new CancellationToken(false);

        // intro
        var introductionAnalyzer = new BaseItemAnalyzerTask(
            AnalysisMode.Introduction,
            _loggerFactory.CreateLogger<Entrypoint>(),
            _loggerFactory,
            _libraryManager);

        introductionAnalyzer.AnalyzeItems(progress, cancellationToken);

        // outro
        var creditsAnalyzer = new BaseItemAnalyzerTask(
            AnalysisMode.Credits,
            _loggerFactory.CreateLogger<Entrypoint>(),
            _loggerFactory,
            _libraryManager);

        creditsAnalyzer.AnalyzeItems(progress, cancellationToken);
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
