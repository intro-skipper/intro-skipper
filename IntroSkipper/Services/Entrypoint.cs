using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services
{
    /// <summary>
    /// Server entrypoint.
    /// </summary>
    public sealed class Entrypoint : IHostedService, IDisposable
    {
        private readonly ITaskManager _taskManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<Entrypoint> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager;
        private readonly HashSet<Guid> _seasonsToAnalyze = [];
        private readonly Timer _queueTimer;
        private static readonly SemaphoreSlim _analysisSemaphore = new(1, 1);
        private PluginConfiguration _config;
        private bool _analyzeAgain;
        private static CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="Entrypoint"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="taskManager">Task manager.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        /// <param name="mediaSegmentUpdateManager">MediaSegment Update Manager.</param>
        public Entrypoint(
            ILibraryManager libraryManager,
            ITaskManager taskManager,
            ILogger<Entrypoint> logger,
            ILoggerFactory loggerFactory,
            MediaSegmentUpdateManager mediaSegmentUpdateManager)
        {
            _libraryManager = libraryManager;
            _taskManager = taskManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _mediaSegmentUpdateManager = mediaSegmentUpdateManager;

            _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            _queueTimer = new Timer(
                    OnTimerCallback,
                    null,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Gets State of the automatic task.
        /// </summary>
        public static TaskState AutomaticTaskState => _cancellationTokenSource switch
        {
            null => TaskState.Idle,
            { IsCancellationRequested: true } => TaskState.Cancelling,
            _ => TaskState.Running
        };

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            _taskManager.TaskCompleted += OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged += OnSettingsChanged;

            FFmpegWrapper.Logger = _logger;

            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager).GetMediaItems();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _taskManager.TaskCompleted -= OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged -= OnSettingsChanged;

            _queueTimer.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Library item was added.
        /// </summary>
        /// <param name="sender">The sending entity.</param>
        /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
        private void OnItemChanged(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            if ((_config.AutoDetectIntros || _config.AutoDetectCredits) &&
                itemChangeEventArgs.Item is { LocationType: not LocationType.Virtual } item)
            {
                Guid? id = item is Episode episode ? episode.SeasonId : (item is Movie movie ? movie.Id : null);

                if (id.HasValue)
                {
                    _seasonsToAnalyze.Add(id.Value);
                    StartTimer();
                }
            }
        }

        /// <summary>
        /// TaskManager task ended.
        /// </summary>
        /// <param name="sender">The sending entity.</param>
        /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
        private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
        {
            if ((_config.AutoDetectIntros || _config.AutoDetectCredits) &&
                eventArgs.Result is { Key: "RefreshLibrary", Status: TaskCompletionStatus.Completed } &&
                AutomaticTaskState != TaskState.Running)
            {
                StartTimer();
            }
        }

        private void OnSettingsChanged(object? sender, BasePluginConfiguration e) => _config = (PluginConfiguration)e;

        /// <summary>
        /// Start timer to debounce analyzing.
        /// </summary>
        private void StartTimer()
        {
            if (AutomaticTaskState == TaskState.Running)
            {
                _analyzeAgain = true;
            }
            else if (AutomaticTaskState == TaskState.Idle)
            {
                _logger.LogDebug("Media Library changed, analyzis will start soon!");
                _queueTimer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            }
        }

        private void OnTimerCallback(object? state) =>
            _ = RunAnalysisAsync();

        private async Task RunAnalysisAsync()
        {
            try
            {
                await PerformAnalysisAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Automatic Analysis task cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RunAnalysisAsync");
            }

            _cancellationTokenSource = null;
        }

        private async Task PerformAnalysisAsync()
        {
            await _analysisSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                using (_cancellationTokenSource = new CancellationTokenSource())
                using (await ScheduledTaskSemaphore.AcquireAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
                {
                    _logger.LogInformation("Initiating automatic analysis task");
                    var seasonIds = new HashSet<Guid>(_seasonsToAnalyze);
                    _seasonsToAnalyze.Clear();
                    _analyzeAgain = false;

                    var modes = new List<AnalysisMode>();

                    if (_config.AutoDetectIntros)
                    {
                        modes.Add(AnalysisMode.Introduction);
                    }

                    if (_config.AutoDetectCredits)
                    {
                        modes.Add(AnalysisMode.Credits);
                    }

                    var analyzer = new BaseItemAnalyzerTask(modes, _loggerFactory.CreateLogger<Entrypoint>(), _loggerFactory, _libraryManager, _mediaSegmentUpdateManager);
                    await analyzer.AnalyzeItems(new Progress<double>(), _cancellationTokenSource.Token, seasonIds).ConfigureAwait(false);

                    if (_analyzeAgain && !_cancellationTokenSource.IsCancellationRequested)
                    {
                        _logger.LogInformation("Analyzing ended, but we need to analyze again!");
                        _queueTimer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                    }
                }
            }
            finally
            {
                _analysisSemaphore.Release();
            }
        }

        /// <summary>
        /// Method to cancel the automatic task.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public static async Task CancelAutomaticTaskAsync(CancellationToken cancellationToken)
        {
            if (_cancellationTokenSource is { IsCancellationRequested: false })
            {
                try
                {
                    await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    _cancellationTokenSource = null;
                }
            }

            await _analysisSemaphore.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _queueTimer.Dispose();
            _cancellationTokenSource?.Dispose();
            _analysisSemaphore.Dispose();
        }
    }
}
