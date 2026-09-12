// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using IntroSkipper.Configuration;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace IntroSkipper.Services
{
    /// <summary>
    /// Server entrypoint: subscribes to library changes and runs the debounced automatic analysis.
    /// Registered as a singleton so <see cref="DetectSegmentsTask"/> can cancel a running automatic pass.
    /// </summary>
    public sealed partial class Entrypoint : IHostedService, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IDetectionCacheDatabase _cacheDatabase;
        private readonly IFFmpegService _ffmpegService;
        private readonly ILogger<Entrypoint> _logger;
        private readonly BaseItemAnalyzerTask _analyzer;
        private readonly HashSet<Guid> _itemsToAnalyze = [];
        private readonly Lock _itemsLock = new();
        private readonly Timer _queueTimer;
        private readonly SemaphoreSlim _analysisSemaphore = new(1, 1);
        private volatile bool _isStopping;
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="Entrypoint"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="cacheDatabase">Detection cache database facade.</param>
        /// <param name="ffmpegService">FFmpeg service.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="analyzer">Analyzer run over the queued items' seasons.</param>
        public Entrypoint(
            ILibraryManager libraryManager,
            IDetectionCacheDatabase cacheDatabase,
            IFFmpegService ffmpegService,
            ILogger<Entrypoint> logger,
            BaseItemAnalyzerTask analyzer)
        {
            _libraryManager = libraryManager;
            _cacheDatabase = cacheDatabase;
            _ffmpegService = ffmpegService;
            _logger = logger;
            _analyzer = analyzer;

            _queueTimer = new Timer(
                    OnTimerCallback,
                    null,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Gets the state of the automatic analysis task.
        /// </summary>
        public TaskState AutomaticTaskState
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

        // Jellyfin replaces the configuration object on save, so the live instance is read on
        // every use instead of a constructor-time snapshot.
        private static PluginConfiguration Config => Plugin.Instance!.Configuration;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_itemsLock)
            {
                _isStopping = false;
            }

            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            _libraryManager.ItemRemoved += OnItemRemoved;

            await _ffmpegService.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);

            // Initialize web injector for skip button timeout modification
            if (Config.FileTransformationPluginEnabled)
            {
                InitializeWebInjector();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_itemsLock)
            {
                _isStopping = true;
                _queueTimer.Change(Timeout.Infinite, 0);
            }

            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemRemoved -= OnItemRemoved;

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

            // Queues the item's own id. The run resolves the season it belongs to when it
            // starts, off this library event thread and after Jellyfin has attached a new
            // episode to its season. A replaced file needs no special handling: queue
            // verification compares the stored file version and reopens the item.
            lock (_itemsLock)
            {
                _itemsToAnalyze.Add(item.Id);
            }

            StartTimer();
        }

        /// <summary>
        /// Library item was removed.
        /// </summary>
        /// <param name="sender">The sending entity.</param>
        /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
        private void OnItemRemoved(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            if (!TryGetValidItemForAutoProcessing(itemChangeEventArgs, out var item) || item.Id == Guid.Empty)
            {
                return;
            }

            LogMediaItemRemoved(item.Id);
            // Best-effort: the facade logs and swallows database errors.
            _cacheDatabase.DeleteForItem(item.Id);
        }

        // An episode or movie with a real location, while automatic analysis is on.
        private static bool TryGetValidItemForAutoProcessing(
            ItemChangeEventArgs itemChangeEventArgs,
            [NotNullWhen(true)] out BaseItem? item)
        {
            item = null;
            if (!Config.AutoDetectIntros)
            {
                return false;
            }

            var candidate = itemChangeEventArgs.Item;
            if (!MediaItemHelper.IsSupported(candidate) || candidate.LocationType == LocationType.Virtual)
            {
                return false;
            }

            item = candidate;
            return true;
        }

        /// <summary>
        /// Start timer to debounce analyzing. Callers queue their work before calling; a
        /// running analysis picks it up through <see cref="ScheduleAnalysisIfNeeded"/> when it ends.
        /// </summary>
        private void StartTimer()
        {
            lock (_itemsLock)
            {
                if (_isStopping || AutomaticTaskState != TaskState.Idle)
                {
                    return;
                }

                LogMediaLibraryChanged();
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
                lock (_itemsLock)
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
                        HashSet<Guid> itemIds;
                        lock (_itemsLock)
                        {
                            itemIds = new HashSet<Guid>(_itemsToAnalyze);
                            _itemsToAnalyze.Clear();
                        }

                        await _analyzer.AnalyzeItemsAsync(new Progress<double>(), cts.Token, itemIds).ConfigureAwait(false);
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
            lock (_itemsLock)
            {
                if (_isStopping)
                {
                    return;
                }

                if (_itemsToAnalyze.Count > 0 && AutomaticTaskState == TaskState.Idle)
                {
                    LogAnalyzingEndedNeedsRestart();
                    _queueTimer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>
        /// Cancels a running automatic analysis and waits for it to finish.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="TimeoutException">The analysis did not finish within 60 seconds.</exception>
        public async Task CancelAutomaticTaskAsync(CancellationToken cancellationToken)
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
            _analysisSemaphore.Dispose();
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media item removed, deleting fingerprint cache for {Id}")]
        private partial void LogMediaItemRemoved(Guid id);

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
