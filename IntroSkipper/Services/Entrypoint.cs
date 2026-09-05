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
        private readonly IIntroSkipperDatabase _database;
        private readonly IFFmpegService _ffmpegService;
        private readonly ILogger<Entrypoint> _logger;
        private readonly AnalyzerTaskFactory _analyzerFactory;
        private readonly HashSet<Guid> _seasonsToAnalyze = [];
        private readonly Dictionary<Guid, Guid> _itemsToReset = [];
        private readonly Lock _seasonsLock = new();
        private readonly Timer _queueTimer;
        private readonly SemaphoreSlim _analysisSemaphore = new(1, 1);
        private volatile bool _isStopping;
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="Entrypoint"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="cacheDatabase">Detection cache database facade.</param>
        /// <param name="database">Segment database facade.</param>
        /// <param name="ffmpegService">FFmpeg service.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="analyzerFactory">Factory for per-run analyzer tasks.</param>
        public Entrypoint(
            ILibraryManager libraryManager,
            IDetectionCacheDatabase cacheDatabase,
            IIntroSkipperDatabase database,
            IFFmpegService ffmpegService,
            ILogger<Entrypoint> logger,
            AnalyzerTaskFactory analyzerFactory)
        {
            _libraryManager = libraryManager;
            _cacheDatabase = cacheDatabase;
            _database = database;
            _ffmpegService = ffmpegService;
            _logger = logger;
            _analyzerFactory = analyzerFactory;

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
            lock (_seasonsLock)
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
            lock (_seasonsLock)
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

            // Episodes queue under their season, movies under their own id.
            var id = item is Episode episode ? episode.SeasonId : item.Id;
            var delay = itemChangeEventArgs.UpdateReason == ItemUpdateType.None ? 120 : 60;

            lock (_seasonsLock)
            {
                // Jellyfin uses ItemUpdateType.None for filesystem-driven item updates. A
                // replacement at the same path retains the Jellyfin item ID, so the normal
                // queue would otherwise treat the old automatic analysis and fingerprint
                // cache as still valid. Defer invalidation until the coordinated analysis
                // pass, after any currently running analysis has finished writing its state.
                if (itemChangeEventArgs.UpdateReason == ItemUpdateType.None &&
                    item.Id != Guid.Empty)
                {
                    _itemsToReset[item.Id] = id;
                }

                _seasonsToAnalyze.Add(id);
            }

            StartTimer(delay);
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
        private void StartTimer(int delay)
        {
            lock (_seasonsLock)
            {
                if (_isStopping || AutomaticTaskState != TaskState.Idle)
                {
                    return;
                }

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
                        }

                        if (itemsToReset.Count > 0)
                        {
                            try
                            {
                                // One batch for every changed item; the reset journals its
                                // deletions' projections and the projection worker converges
                                // Jellyfin durably. The cache delete follows the committed
                                // reset uncancelled so the two cannot be split by cancellation.
                                await _database.ResetItemsForReanalysisAsync(
                                    itemsToReset.Keys,
                                    Enum.GetValues<AnalysisMode>(),
                                    cts.Token).ConfigureAwait(false);
                                await _cacheDatabase.DeleteForItemsAsync(itemsToReset.Keys, CancellationToken.None).ConfigureAwait(false);

                                foreach (var itemId in itemsToReset.Keys)
                                {
                                    LogMediaItemChanged(_logger, itemId);
                                }
                            }
                            catch (Exception ex)
                            {
                                // Requeue the whole batch and skip its seasons this pass.
                                lock (_seasonsLock)
                                {
                                    foreach (var (itemId, seasonId) in itemsToReset)
                                    {
                                        _itemsToReset[itemId] = seasonId;
                                        _seasonsToAnalyze.Add(seasonId);
                                    }
                                }

                                seasonIds.ExceptWith(itemsToReset.Values);
                                LogErrorResettingChangedMediaItems(_logger, ex, itemsToReset.Count);
                            }
                        }

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

                var needsRestart = _seasonsToAnalyze.Count > 0 || _itemsToReset.Count > 0;
                if (needsRestart && AutomaticTaskState == TaskState.Idle)
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

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media item changed, invalidating automatic analysis and fingerprint cache for {Id}")]
        private static partial void LogMediaItemChanged(ILogger logger, Guid id);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to invalidate automatic analysis for {Count} changed media items")]
        private static partial void LogErrorResettingChangedMediaItems(ILogger logger, Exception ex, int count);

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
