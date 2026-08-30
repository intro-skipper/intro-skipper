// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
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
        private readonly IDetectionCacheDatabase _cacheDatabase;
        private readonly IIntroSkipperDatabase _database;
        private readonly IFFmpegService _ffmpegService;
        private readonly ILogger<Entrypoint> _logger;
        private readonly AnalyzerTaskFactory _analyzerFactory;
        private readonly HashSet<Guid> _seasonsToAnalyze = [];
        private readonly Dictionary<Guid, Guid> _itemsToReset = [];
        private readonly Lock _seasonsLock = new();
        private readonly IMediaSegmentRefresher _mediaSegmentRefresher;
        private readonly Timer _queueTimer;
        private static readonly SemaphoreSlim _analysisSemaphore = new(1, 1);
        private PluginConfiguration _config;
        private volatile bool _analyzeAgain;
        private volatile bool _isStopping;
        private static CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="Entrypoint"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="taskManager">Task manager.</param>
        /// <param name="cacheDatabase">Detection cache database facade.</param>
        /// <param name="database">Segment database facade.</param>
        /// <param name="ffmpegService">FFmpeg service.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="analyzerFactory">Factory for per-run analyzer tasks.</param>
        /// <param name="mediaSegmentRefresher">Media segment refresher.</param>
        public Entrypoint(
            ILibraryManager libraryManager,
            ITaskManager taskManager,
            IDetectionCacheDatabase cacheDatabase,
            IIntroSkipperDatabase database,
            IFFmpegService ffmpegService,
            ILogger<Entrypoint> logger,
            AnalyzerTaskFactory analyzerFactory,
            IMediaSegmentRefresher mediaSegmentRefresher)
        {
            _libraryManager = libraryManager;
            _taskManager = taskManager;
            _cacheDatabase = cacheDatabase;
            _database = database;
            _ffmpegService = ffmpegService;
            _logger = logger;
            _analyzerFactory = analyzerFactory;
            _mediaSegmentRefresher = mediaSegmentRefresher;

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
        public static TaskState AutomaticTaskState
        {
            get
            {
                var cts = Volatile.Read(ref _cancellationTokenSource);
                return cts switch
                {
                    null => TaskState.Idle,
                    { IsCancellationRequested: true } => TaskState.Cancelling,
                    _ => TaskState.Running
                };
            }
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_seasonsLock)
            {
                _isStopping = false;
            }

            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _taskManager.TaskCompleted += OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged += OnSettingsChanged;

            await _ffmpegService.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);

            // Initialize web injector for skip button timeout modification
            if (_config.FileTransformationPluginEnabled == true)
            {
                InitializeWebInjector();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_seasonsLock)
            {
                _isStopping = true;
                _queueTimer.Change(Timeout.Infinite, 0);
            }

            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            _taskManager.TaskCompleted -= OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged -= OnSettingsChanged;

            await CancelAutomaticTaskAsync(cancellationToken).ConfigureAwait(false);
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

                lock (_seasonsLock)
                {
                    // Jellyfin uses ItemUpdateType.None for filesystem-driven item updates. A
                    // replacement at the same path retains the Jellyfin item ID, so the normal
                    // queue would otherwise treat the old automatic analysis and fingerprint
                    // cache as still valid. Defer invalidation until the coordinated analysis
                    // pass, after any currently running analysis has finished writing its state.
                    if (itemChangeEventArgs.UpdateReason == ItemUpdateType.None &&
                        item.Id != Guid.Empty &&
                        Plugin.Instance is not null)
                    {
                        _itemsToReset[item.Id] = id.Value;
                    }

                    _seasonsToAnalyze.Add(id.Value);
                }

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
                _cacheDatabase.DeleteForItem(id.Value);
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
            catch (Exception ex)
            {
                // LocationType can throw on partially-initialized items (e.g. in unit tests).
                LogLocationTypeEvaluationFailed(ex);
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
            if (Plugin.Instance is { } plugin)
            {
                plugin.AnalyzeAgain = true;
            }
        }

        /// <summary>
        /// Start timer to debounce analyzing.
        /// </summary>
        private void StartTimer(int delay = 60)
        {
            lock (_seasonsLock)
            {
                if (_isStopping)
                {
                    return;
                }

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
        }

        private async Task PerformAnalysisAsync()
        {
            await _analysisSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var cts = new CancellationTokenSource();

                // A timer callback can already be in flight when StopAsync stops the timer.
                // Starting a new analysis here would leave shutdown waiting on the semaphore
                // until it times out, so bail out and keep the queues for the next start.
                // Checking the flag and publishing the cancellation source under the same
                // lock StopAsync uses to set the flag guarantees shutdown either sees the
                // published source and cancels it, or this callback sees the flag and stops.
                lock (_seasonsLock)
                {
                    if (_isStopping)
                    {
                        cts.Dispose();
                        return;
                    }

                    Interlocked.Exchange(ref _cancellationTokenSource, cts);
                }

                try
                {
                    using (await ScheduledTaskSemaphore.AcquireAsync(cts.Token).ConfigureAwait(false))
                    {
                        LogInitiatingAutomaticAnalysis();
                        HashSet<Guid> seasonIds;
                        Dictionary<Guid, Guid> itemsToReset;
                        lock (_seasonsLock)
                        {
                            seasonIds = new HashSet<Guid>(_seasonsToAnalyze);
                            itemsToReset = new Dictionary<Guid, Guid>(_itemsToReset);
                            _seasonsToAnalyze.Clear();
                            _itemsToReset.Clear();
                            _analyzeAgain = false;
                        }

                        var failedResetSeasonIds = new HashSet<Guid>();
                        var successfullyResetItems = new Dictionary<Guid, Guid>();
                        foreach (var (itemId, seasonId) in itemsToReset)
                        {
                            try
                            {
                                await _database.ResetItemsForReanalysisAsync(
                                    [itemId],
                                    Enum.GetValues<AnalysisMode>(),
                                    cts.Token).ConfigureAwait(false);
                                _cacheDatabase.DeleteForItem(itemId);

                                successfullyResetItems[itemId] = seasonId;
                                LogMediaItemChanged(_logger, itemId);
                            }
                            catch (Exception ex)
                            {
                                lock (_seasonsLock)
                                {
                                    _itemsToReset[itemId] = seasonId;
                                    _seasonsToAnalyze.Add(seasonId);
                                    _analyzeAgain = true;
                                }

                                failedResetSeasonIds.Add(seasonId);
                                LogErrorResettingChangedMediaItem(_logger, ex, itemId);
                            }
                        }

                        var failedRefreshSeasonIds = new HashSet<Guid>();
                        if (_config.UpdateMediaSegments && successfullyResetItems.Count > 0)
                        {
                            try
                            {
                                await _mediaSegmentRefresher.RefreshAsync(successfullyResetItems.Keys, cts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cts.IsCancellationRequested)
                            {
                                RequeueResetItems(successfullyResetItems);
                                throw;
                            }
                            catch (Exception ex)
                            {
                                RequeueResetItems(successfullyResetItems);
                                failedRefreshSeasonIds.UnionWith(successfullyResetItems.Values);
                                LogErrorRefreshingChangedMediaItems(_logger, ex, successfullyResetItems.Count);
                            }
                        }

                        seasonIds.ExceptWith(failedResetSeasonIds);
                        seasonIds.ExceptWith(failedRefreshSeasonIds);

                        var analyzer = _analyzerFactory.CreateAnalyzerTask();
                        await analyzer.AnalyzeItemsAsync(new Progress<double>(), cts.Token, seasonIds).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Null the field BEFORE disposing to prevent other threads
                    // from reading a disposed CancellationTokenSource via Volatile.Read.
                    Interlocked.Exchange(ref _cancellationTokenSource, null);
                    cts.Dispose();

                    // Do this after making the task idle. An item update can arrive while the
                    // task is cancelling; checking the queues here ensures that update is not
                    // stranded. ScheduleAnalysisIfNeeded suppresses this during shutdown.
                    ScheduleAnalysisIfNeeded();
                }
            }
            finally
            {
                _analysisSemaphore.Release();
            }
        }

        private void ScheduleAnalysisIfNeeded()
        {
            lock (_seasonsLock)
            {
                if (_isStopping)
                {
                    return;
                }

                var needsRestart = _analyzeAgain || _seasonsToAnalyze.Count > 0 || _itemsToReset.Count > 0;
                if (needsRestart && AutomaticTaskState == TaskState.Idle)
                {
                    LogAnalyzingEndedNeedsRestart();
                    _queueTimer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void RequeueResetItems(IReadOnlyDictionary<Guid, Guid> itemsToReset)
        {
            lock (_seasonsLock)
            {
                foreach (var (itemId, seasonId) in itemsToReset)
                {
                    _itemsToReset[itemId] = seasonId;
                    _seasonsToAnalyze.Add(seasonId);
                }

                _analyzeAgain = true;
            }
        }

        /// <summary>
        /// Method to cancel the automatic task.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public static async Task CancelAutomaticTaskAsync(CancellationToken cancellationToken)
        {
            var cts = Volatile.Read(ref _cancellationTokenSource);
            if (cts is { IsCancellationRequested: false })
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.CompareExchange(ref _cancellationTokenSource, null, cts);
                }
            }

            if (!await _analysisSemaphore.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("Timed out waiting for the automatic analysis task to complete.");
            }

            _analysisSemaphore.Release();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _queueTimer.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media item removed, deleting fingerprint cache for {Id}")]
        private partial void LogMediaItemRemoved(Guid id);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media item changed, invalidating automatic analysis and fingerprint cache for {Id}")]
        private static partial void LogMediaItemChanged(ILogger logger, Guid id);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to invalidate automatic analysis for changed media item {Id}")]
        private static partial void LogErrorResettingChangedMediaItem(ILogger logger, Exception ex, Guid id);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to refresh media segments for {Count} changed media items")]
        private static partial void LogErrorRefreshingChangedMediaItems(ILogger logger, Exception ex, int count);

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

        [LoggerMessage(Level = LogLevel.Debug, Message = "LocationType evaluation failed for item")]
        private partial void LogLocationTypeEvaluationFailed(Exception ex);
    }
}
