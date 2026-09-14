// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading.Channels;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.SegmentChanges;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// The analysis queue: runs every analysis pass, one at a time, on one worker. Feeders
/// enqueue a request and get a handle that completes with the pass that covered it, is
/// cancelled when shutdown drops it, and faults when the pass threw.
/// </summary>
/// <remarks>
/// Three kinds of request. Changed items (from the library watcher) run together once the
/// quiet period has elapsed since the most recent change. The window slides on every change
/// and is measured from the change, so an item that waited behind a running pass runs as
/// soon as that pass ends. A manual scan (from the dashboard) runs as its own pass, erase
/// then analyze, ahead of any pending library pass and as soon as the worker is free;
/// nothing absorbs it, a repeated key joins the pending scan of that key, one requested
/// while its scan runs queues a follow-up pass, and the season is resolved when the pass
/// starts, not when the scan was requested. A library pass (from the scheduled task)
/// starts once the worker is free and every pending manual scan has run. It covers no
/// pending request: changed items still run in their own pass once due, a cheap one when
/// the library pass already analyzed them. A pass waits for the pass in flight; only the
/// scheduled task's own token or shutdown cancels a running pass. Requests arriving during
/// a pass wait for the next one. Pending requests are dropped on shutdown.
/// </remarks>
/// <param name="analyzer">Analyzer run by every pass.</param>
/// <param name="seasonResolver">Resolver of the season a manual scan erases and analyzes.</param>
/// <param name="eraser">Erases a manual scan's season before its analysis.</param>
/// <param name="timeProvider">Clock for the quiet period.</param>
/// <param name="logger">Logger.</param>
public sealed partial class AnalysisScheduler(
    BaseItemAnalyzerTask analyzer,
    SeasonResolver seasonResolver,
    ISegmentEraser eraser,
    TimeProvider timeProvider,
    ILogger<AnalysisScheduler> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// The time changed items wait after the most recent change before their pass starts.
    /// </summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);

    private static readonly Progress<double> NoProgress = new();

    private readonly BaseItemAnalyzerTask _analyzer = analyzer;
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly ISegmentEraser _eraser = eraser;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AnalysisScheduler> _logger = logger;
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _changedItems = [];
    private readonly List<ManualScan> _manualScans = [];

    // Keys whose most recent completed manual scan failed, so the dashboard can tell a
    // failed scan from a finished one after the fact. Updated when a manual pass
    // completes, never when one is queued: a follow-up queued while a scan runs must
    // report its own result, not the outcome of the scan it waited behind.
    private readonly HashSet<Guid> _failedScans = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stopping = new();

    // One handle for the pending changed items; present exactly while the set is non-empty.
    private TaskCompletionSource? _changedCompletion;
    private DateTimeOffset _lastChange;
    private LibraryRequest? _libraryRequest;
    private Pass? _running;
    private Task? _worker;
    private bool _stopped;

    private enum PassKind
    {
        ChangedItems,
        ManualScan,
        Library,
    }

    private enum PassOutcome
    {
        Completed,
        Cancelled,
        Failed,
    }

    /// <summary>
    /// Gets what the queue holds and does right now.
    /// </summary>
    public AnalysisSchedulerStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new AnalysisSchedulerStatus(_running is not null, _changedItems.Count, _manualScans.Count, _libraryRequest is not null);
            }
        }
    }

    /// <summary>
    /// Returns the state of the key's manual scan: whether one is pending or running, and
    /// whether the most recent completed one failed.
    /// </summary>
    /// <param name="key">A season key.</param>
    /// <returns>The scan state.</returns>
    public ManualScanStatus ScanStatus(Guid key)
    {
        lock (_lock)
        {
            return new ManualScanStatus(
                Queued: _running?.ScanKey == key || _manualScans.Exists(scan => scan.Key == key),
                Failed: _failedScans.Contains(key));
        }
    }

    /// <summary>
    /// Queues a changed library item. Its season is resolved when the pass starts, after
    /// the quiet period since the most recent change.
    /// </summary>
    /// <param name="itemId">The id of the changed episode or movie.</param>
    /// <returns>A task that completes with the pass that covers the item.</returns>
    public Task ItemChangedAsync(Guid itemId)
    {
        TaskCompletionSource completion;
        bool wake;
        lock (_lock)
        {
            if (_stopped)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            // The worker sleeps without a timer only while no item is pending. Once one
            // is, its timer fires at the old due time and re-arms at the new one, so only
            // the first change of the pending pass needs to wake it.
            wake = _changedCompletion is null;
            completion = _changedCompletion ??= NewCompletion();
            _changedItems.Add(itemId);
            _lastChange = _timeProvider.GetUtcNow();
        }

        if (wake)
        {
            Wake();
        }

        return completion.Task;
    }

    /// <summary>
    /// Queues a manual scan: its own pass that resolves the season the dashboard shows
    /// under the key, erases it, then analyzes it, ahead of any pending library pass. An
    /// erase failure faults the scan. A scan for a key that is already pending joins it.
    /// One requested while the key's scan runs queues a follow-up pass: the running pass
    /// may already have taken its inventory, so it cannot cover work requested after
    /// that. A key that no longer resolves to a season when the pass starts completes
    /// without running.
    /// </summary>
    /// <param name="key">The season key the scan is requested for.</param>
    /// <returns>A task that completes with the scan's pass.</returns>
    internal Task ScanAsync(Guid key)
    {
        var completion = NewCompletion();
        bool queued;
        lock (_lock)
        {
            if (_stopped)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            var pending = _manualScans.Find(scan => scan.Key == key);
            queued = pending is null;
            if (pending is null)
            {
                pending = new ManualScan(key, []);
                _manualScans.Add(pending);
            }

            pending.Completions.Add(completion);
        }

        if (queued)
        {
            Wake();
        }

        return completion.Task;
    }

    /// <summary>
    /// Runs a pass over every enabled library and waits for it. The pass starts once the
    /// worker is free and every pending manual scan has run. One request at a time:
    /// Jellyfin's task worker never starts a task that is still running.
    /// </summary>
    /// <param name="progress">Progress of the pass.</param>
    /// <param name="cancellationToken">Cancels this request only. While it waits it is withdrawn and the call returns at once; once its pass runs, the pass is cancelled and the call returns when it has stopped. Other requests stay queued.</param>
    /// <returns>A task that completes when the pass has run.</returns>
    /// <exception cref="OperationCanceledException">The request was cancelled, or the queue has stopped.</exception>
    /// <exception cref="InvalidOperationException">A library pass is already requested and has not started.</exception>
    public async Task RunLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var request = new LibraryRequest(progress, NewCompletion(), cancellationToken);
        lock (_lock)
        {
            if (_stopped)
            {
                throw new OperationCanceledException("The analysis queue has stopped.");
            }

            if (_libraryRequest is not null)
            {
                throw new InvalidOperationException("A library pass is already requested.");
            }

            _libraryRequest = request;
        }

        Wake();

        // Cancelling withdraws the request only while it is pending, so a later request
        // never inherits a cancelled one. A pass already running sees the token through
        // its linked source and settles the handle once it has stopped, so Jellyfin's
        // task list and the dashboard agree on when the pass ended.
        using var withdrawal = cancellationToken.Register(() => Withdraw(request));
        await request.Completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => RunWorkerAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<TaskCompletionSource> dropped;
        lock (_lock)
        {
            _stopped = true;
            dropped = [.. _manualScans.SelectMany(scan => scan.Completions)];
            if (_changedCompletion is { } changed)
            {
                dropped.Add(changed);
            }

            if (_libraryRequest is { } library)
            {
                dropped.Add(library.Completion);
            }

            _changedItems.Clear();
            _changedCompletion = null;
            _manualScans.Clear();
            _libraryRequest = null;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        // Dropped requests are cancelled before the wait on the worker, so a host deadline
        // that expires while a pass ignores cancellation still leaves no handle pending.
        foreach (var completion in dropped)
        {
            completion.TrySetCanceled(_stopping.Token);
        }

        if (dropped.Count > 0)
        {
            LogStoppedWithPendingRequests(_logger, dropped.Count);
        }

        if (_worker is { } worker)
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => _wake.Writer.TryWrite(true);

    // Withdraws a library request that is still pending. One whose pass already runs is
    // left to the pass, which settles it when it has stopped.
    private void Withdraw(LibraryRequest request)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_libraryRequest, request))
            {
                return;
            }

            _libraryRequest = null;
        }

        request.Completion.TrySetCanceled(request.CancellationToken);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var (pass, due) = TakeNext();
                if (pass is not null)
                {
                    await RunPassAsync(pass, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await WaitForWorkAsync(due, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // The next pass to run, or when the pending changed items become due. The pass
    // becomes the running one under the lock that dequeued it, so the status never shows
    // its request as neither pending nor running.
    private (Pass? Pass, DateTimeOffset? Due) TakeNext()
    {
        lock (_lock)
        {
            var (pass, due) = Dequeue();
            _running = pass;
            return (pass, due);
        }
    }

    // Under _lock. Manual scans go first, then the library pass, then changed items once
    // their quiet period has elapsed.
    private (Pass? Pass, DateTimeOffset? Due) Dequeue()
    {
        if (_manualScans.Count > 0)
        {
            var scan = _manualScans[0];
            _manualScans.RemoveAt(0);
            return (new Pass(PassKind.ManualScan, scan.Key, cancellationToken => RunScanAsync(scan.Key, cancellationToken), scan.Completions, CancellationToken.None), null);
        }

        if (_libraryRequest is { } library)
        {
            _libraryRequest = null;
            return (new Pass(PassKind.Library, null, cancellationToken => _analyzer.AnalyzeItemsAsync(library.Progress, cancellationToken), [library.Completion], library.CancellationToken), null);
        }

        if (_changedCompletion is not { } changed)
        {
            return (null, null);
        }

        var due = _lastChange + QuietPeriod;
        if (_timeProvider.GetUtcNow() < due)
        {
            return (null, due);
        }

        var itemIds = _changedItems.ToHashSet();
        _changedItems.Clear();
        _changedCompletion = null;
        return (new Pass(PassKind.ChangedItems, null, cancellationToken => _analyzer.AnalyzeItemsAsync(NoProgress, cancellationToken, itemIds), [changed], CancellationToken.None), null);
    }

    // Waits for a request, or until the pending changed items are due, whichever is first.
    private async Task WaitForWorkAsync(DateTimeOffset? due, CancellationToken stoppingToken)
    {
        if (due is null)
        {
            await _wake.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        var delay = due.Value - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(delay, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            await _wake.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunPassAsync(Pass pass, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, pass.CancellationToken);
        var cancellationToken = linked.Token;
        LogPassStarting(_logger, pass.Kind);
        var outcome = PassOutcome.Completed;
        Exception? failure = null;
        try
        {
            await pass.Run(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogPassCancelled(_logger, pass.Kind);
            outcome = PassOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            LogPassFailed(_logger, ex, pass.Kind);
            outcome = PassOutcome.Failed;
            failure = ex;
        }

        // The pass is over before its handles complete, so whoever awaits one sees the
        // queue idle, and a manual scan's result is recorded first, so a poll that finds
        // the scan gone reads this pass's outcome. A cancelled pass leaves the record
        // of the last completed one.
        lock (_lock)
        {
            _running = null;
            if (pass.ScanKey is { } key && outcome is not PassOutcome.Cancelled)
            {
                if (outcome is PassOutcome.Failed)
                {
                    _failedScans.Add(key);
                }
                else
                {
                    _failedScans.Remove(key);
                }
            }
        }

        foreach (var completion in pass.Completions)
        {
            switch (outcome)
            {
                case PassOutcome.Completed:
                    completion.TrySetResult();
                    break;
                case PassOutcome.Cancelled:
                    completion.TrySetCanceled(cancellationToken);
                    break;
                default:
                    // The failure is logged above, so a feeder may drop its handle. Reading
                    // the exception marks it observed and keeps a dropped handle out of
                    // TaskScheduler.UnobservedTaskException; an awaited one still throws.
                    completion.TrySetException(failure!);
                    _ = completion.Task.Exception;
                    break;
            }
        }
    }

    // The season is resolved now, not when the scan was requested, so a season that
    // changed while the scan waited is erased and analyzed as it is. A failed erase
    // propagates: with the analysis records still in place the analyzer would find
    // nothing to do, and the scan would report success having changed nothing.
    private async Task RunScanAsync(Guid key, CancellationToken cancellationToken)
    {
        if (_seasonResolver.ResolveDisplayed(key) is not { } season)
        {
            LogScanSeasonGone(_logger, key);
            return;
        }

        var itemIds = season.Episodes.Select(episode => episode.EpisodeId).ToHashSet();
        await _eraser.EraseItemsAsync(itemIds, eraseCache: true, cancellationToken).ConfigureAwait(false);
        await _analyzer.AnalyzeItemsAsync(NoProgress, cancellationToken, itemIds).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting analysis pass: {Kind}")]
    private static partial void LogPassStarting(ILogger logger, PassKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis pass cancelled: {Kind}")]
    private static partial void LogPassCancelled(ILogger logger, PassKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis pass failed: {Kind}")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, PassKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping the manual scan of {Key}: it no longer resolves to a season")]
    private static partial void LogScanSeasonGone(ILogger logger, Guid key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis queue stopped; {Count} pending requests dropped")]
    private static partial void LogStoppedWithPendingRequests(ILogger logger, int count);

    private sealed record ManualScan(Guid Key, List<TaskCompletionSource> Completions);

    private sealed record LibraryRequest(IProgress<double> Progress, TaskCompletionSource Completion, CancellationToken CancellationToken);

    // What the worker runs. Kind names it in the log; ScanKey is set for a manual scan so
    // the status can report the key running and a failure is remembered against it.
    private sealed record Pass(
        PassKind Kind,
        Guid? ScanKey,
        Func<CancellationToken, Task> Run,
        List<TaskCompletionSource> Completions,
        CancellationToken CancellationToken);
}

/// <summary>
/// What the analysis queue holds and does. <see cref="IsRunning"/> is the dashboard's
/// definition: a pass in flight, or a manual scan or library pass pending. A changed item
/// waiting out its quiet period does not count, so a library change never locks the scan
/// button.
/// </summary>
/// <param name="PassRunning">Whether a pass is in flight.</param>
/// <param name="ChangedItems">The number of changed items waiting for their pass.</param>
/// <param name="ManualScans">The number of manual scans waiting for their pass.</param>
/// <param name="LibraryPending">Whether a library pass is waiting.</param>
public sealed record AnalysisSchedulerStatus(bool PassRunning, int ChangedItems, int ManualScans, bool LibraryPending)
{
    /// <summary>
    /// Gets a value indicating whether the dashboard should report a scan as running.
    /// </summary>
    public bool IsRunning => PassRunning || ManualScans > 0 || LibraryPending;
}

/// <summary>
/// The state of one key's manual scan, which the dashboard polls until its scan has run.
/// </summary>
/// <param name="Queued">Whether a scan of the key is pending or running.</param>
/// <param name="Failed">Whether the key's most recent completed scan failed.</param>
public sealed record ManualScanStatus(bool Queued, bool Failed);
