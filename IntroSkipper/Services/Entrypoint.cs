// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace IntroSkipper.Services
{
    /// <summary>
    /// Server entrypoint.
    /// </summary>
    public sealed partial class Entrypoint : IHostedService, IDisposable
    {
        private readonly ITaskManager _taskManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<Entrypoint> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _serviceProvider;
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
        /// <param name="providerManager">Provider manager.</param>
        /// <param name="fileSystem">File system.</param>
        /// <param name="taskManager">Task manager.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        /// <param name="serviceProvider">Service provider.</param>
        public Entrypoint(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ITaskManager taskManager,
            ILogger<Entrypoint> logger,
            ILoggerFactory loggerFactory,
            IServiceProvider serviceProvider)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _taskManager = taskManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _serviceProvider = serviceProvider;

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
            _libraryManager.ItemRemoved += OnItemRemoved;
            _taskManager.TaskCompleted += OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged += OnSettingsChanged;

            FFmpegWrapper.Logger = _logger;
            FFmpegWrapper.CheckFFmpegVersion();

            // Initialize web injector for skip button timeout modification
            if (_config.FileTransformationPluginEnabled == true)
            {
                InitializeWebInjector();
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            _taskManager.TaskCompleted -= OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged -= OnSettingsChanged;

            _queueTimer.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Initializes the web injector for skip button timeout modification.
        /// </summary>
        private void InitializeWebInjector()
        {
            JObject payload = new JObject
            {
                { "id", "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b" },
                { "fileNamePattern", "main.jellyfin.bundle.js" },
                { "callbackAssembly", GetType().Assembly.FullName },
                { "callbackClass", typeof(Injector).FullName },
                { "callbackMethod", nameof(Injector.FileTransformer) }
            };

            Assembly? fileTransformationAssembly =
                AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x =>
                    x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

            if (fileTransformationAssembly is not null)
            {
                Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

                pluginInterfaceType?.GetMethod("RegisterTransformation")?.Invoke(null, [payload]);
            }
        }

        /// <summary>
        /// Library item was added.
        /// </summary>
        /// <param name="sender">The sending entity.</param>
        /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
        private void OnItemChanged(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            if (itemChangeEventArgs.UpdateReason == ItemUpdateType.ImageUpdate)
            {
                return;
            }

            if (!TryGetValidItemForAutoProcessing(itemChangeEventArgs, out var item))
            {
                return;
            }

            Guid? id = item switch
            {
                Episode episode => episode.SeasonId,
                Movie movie => movie.Id,
                _ => null
            };

            if (id.HasValue)
            {
                var delay = itemChangeEventArgs.UpdateReason == 0 ? 120 : 60;

                _seasonsToAnalyze.Add(id.Value);
                StartTimer(delay);
            }
        }

        /// <summary>
        /// Library item was removed.
        /// </summary>
        /// <param name="sender">The sending entity.</param>
        /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
        private void OnItemRemoved(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            try
            {
                if (!TryGetValidItemForAutoProcessing(itemChangeEventArgs, out var item))
                {
                    return;
                }

                Guid? id = item switch
                {
                    Episode episode => episode.Id,
                    Movie movie => movie.Id,
                    _ => null
                };

                if (!id.HasValue || id.Value == Guid.Empty)
                {
                    return;
                }

                LogMediaItemRemoved(id.Value);
                FFmpegWrapper.DeleteFingerprintCache(id.Value);
            }
            catch (Exception ex)
            {
                LogErrorDeletingFingerprintCache(ex);
            }
        }

        private bool TryGetValidItemForAutoProcessing(
            ItemChangeEventArgs itemChangeEventArgs,
            [NotNullWhen(true)] out BaseItem? item)
        {
            if (!_config.AutoDetectIntros)
            {
                item = null;
                return false;
            }

            var candidate = itemChangeEventArgs.Item;
            if (candidate is null)
            {
                item = null;
                return false;
            }

            // Needed for unit tests: avoid analyzing for virtual items, but don't fail if the item
            // is partially initialized.
            try
            {
                if (candidate.LocationType == LocationType.Virtual)
                {
                    item = null;
                    return false;
                }
            }
            catch
            {
                // Ignore LocationType evaluation issues.
            }

            item = candidate;
            return true;
        }

        /// <summary>
        /// TaskManager task ended.
        /// </summary>
        /// <param name="sender">The sending entity.</param>
        /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
        private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
        {
            if (_config.AutoDetectIntros &&
                eventArgs.Result is { Key: "RefreshLibrary", Status: TaskCompletionStatus.Completed } &&
                AutomaticTaskState != TaskState.Running)
            {
                StartTimer();
            }
        }

        private void OnSettingsChanged(object? sender, BasePluginConfiguration e)
        {
            _config = (PluginConfiguration)e;
            Plugin.Instance!.AnalyzeAgain = true;
        }

        /// <summary>
        /// Start timer to debounce analyzing.
        /// </summary>
        private void StartTimer(int delay = 60)
        {
            if (AutomaticTaskState == TaskState.Running)
            {
                _analyzeAgain = true;
            }
            else if (AutomaticTaskState == TaskState.Idle)
            {
                LogMediaLibraryChanged();
                _queueTimer.Change(TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
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
                LogAutomaticAnalysisCancelled();
            }
            catch (Exception ex)
            {
                LogRunAnalysisError(ex);
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
                    LogInitiatingAutomaticAnalysis();
                    var seasonIds = new HashSet<Guid>(_seasonsToAnalyze);
                    _seasonsToAnalyze.Clear();
                    _analyzeAgain = false;

                    var analyzer = new BaseItemAnalyzerTask(_loggerFactory.CreateLogger<Entrypoint>(), _loggerFactory, _libraryManager, _serviceProvider);
                    await analyzer.AnalyzeItemsAsync(new Progress<double>(), _cancellationTokenSource.Token, seasonIds).ConfigureAwait(false);

                    if (_analyzeAgain && !_cancellationTokenSource.IsCancellationRequested)
                    {
                        LogAnalyzingEndedNeedsRestart();
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

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media item removed, deleting fingerprint cache for {Id}")]
        private partial void LogMediaItemRemoved(Guid id);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error deleting fingerprint cache on item removal")]
        private partial void LogErrorDeletingFingerprintCache(Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media Library changed, analysis will start soon!")]
        private partial void LogMediaLibraryChanged();

        [LoggerMessage(Level = LogLevel.Information, Message = "Automatic Analysis task cancelled")]
        private partial void LogAutomaticAnalysisCancelled();

        [LoggerMessage(Level = LogLevel.Error, Message = "Error in RunAnalysisAsync")]
        private partial void LogRunAnalysisError(Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Initiating automatic analysis task")]
        private partial void LogInitiatingAutomaticAnalysis();

        [LoggerMessage(Level = LogLevel.Information, Message = "Analyzing ended, but we need to analyze again!")]
        private partial void LogAnalyzingEndedNeedsRestart();
    }
}
