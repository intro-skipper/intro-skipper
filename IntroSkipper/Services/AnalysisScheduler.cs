// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading.Channels;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Runs every analysis pass, one at a time, on one worker. Feeders enqueue a request and
/// get a handle that completes with the pass that covered it, is cancelled when shutdown
/// drops it, and faults when the pass threw.
/// </summary>
/// <remarks>
/// Three kinds of request. Changed items (from the library watcher) run together once the
/// quiet period has elapsed since the most recent change. The window slides on every change
/// and is measured from the change, so an item that waited behind a running pass runs as
/// soon as that pass ends. A manual scan (from the dashboard) runs as its own pass, erase
/// then analyze, ahead of any pending library pass and as soon as the worker is free;
/// nothing absorbs it, and a repeated key merges into the pending scan. A library pass
/// (from the scheduled task) starts once the worker is free and every pending manual scan
/// has run; it absorbs the pending changed items, which complete with it. A pass waits for
/// the pass in flight; only the scheduled task's own token or shutdown cancels a running
/// pass. Requests arriving during a pass wait for the next one. Pending requests are
/// dropped on shutdown.
/// </remarks>
/// <param name="analyzer">Analyzer run by every pass.</param>
/// <param name="timeProvider">Clock for the quiet period.</param>
/// <param name="logger">Logger.</param>
public sealed partial class AnalysisScheduler(BaseItemAnalyzerTask analyzer, TimeProvider timeProvider, ILogger<AnalysisScheduler> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// The time changed items wait after the most recent change before their pass starts.
    /// </summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);

    private static readonly Progress<double> NoProgress = new();

    private readonly BaseItemAnalyzerTask _analyzer = analyzer;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AnalysisScheduler> _logger = logger;
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _changedItems = [];
    private readonly List<TaskCompletionSource> _changedCompletions = [];
    private readonly List<ManualScan> _manualScans = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stopping = new();
    private DateTimeOffset _lastChange;
    private LibraryPass? _libraryPass;
    private Task? _worker;
    private volatile bool _passRunning;
    private bool _stopped;

    private enum PassKind
    {
        ChangedItems,
        ManualScan,
        Library,
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
                return new AnalysisSchedulerStatus(_passRunning, _changedItems.Count, _manualScans.Count, _libraryPass is not null);
            }
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
        var completion = NewCompletion();
        lock (_lock)
        {
            if (_stopped)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            _changedItems.Add(itemId);
            _changedCompletions.Add(completion);
            _lastChange = _timeProvider.GetUtcNow();
        }

        Wake();
        return completion.Task;
    }

    /// <summary>
    /// Queues a manual scan: its own pass that erases first and then analyzes, ahead of any
    /// pending library pass. An erase failure is logged and the analysis still runs. A scan
    /// for a key that is already pending merges into it.
    /// </summary>
    /// <param name="key">The season key the scan is requested for.</param>
    /// <param name="itemIds">The ids of the episodes or movie to erase and analyze.</param>
    /// <param name="erase">Erases the items' timestamps and cache; runs on the worker before the analysis.</param>
    /// <returns>A task that completes with the scan's pass.</returns>
    public Task ScanAsync(Guid key, IReadOnlyCollection<Guid> itemIds, Func<CancellationToken, Task> erase)
    {
        var completion = NewCompletion();
        lock (_lock)
        {
            if (_stopped)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            if (_manualScans.Find(scan => scan.Key == key) is { } pending)
            {
                pending.Completions.Add(completion);
            }
            else
            {
                _manualScans.Add(new ManualScan(key, itemIds, erase, [completion]));
            }
        }

        Wake();
        return completion.Task;
    }

    /// <summary>
    /// Runs a pass over every enabled library and waits for it. The pass starts once the
    /// worker is free and every pending manual scan has run, and absorbs the pending
    /// changed items. A second request while one is pending shares its pass.
    /// </summary>
    /// <param name="progress">Progress of the pass.</param>
    /// <param name="cancellationToken">Cancels this pass only, pending or running; other requests stay queued.</param>
    /// <returns>A task that completes when the pass has run.</returns>
    /// <exception cref="OperationCanceledException">The pass was cancelled, or the queue has stopped.</exception>
    public async Task RunLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var completion = NewCompletion();
        lock (_lock)
        {
            if (_stopped)
            {
                throw new OperationCanceledException("The analysis queue has stopped.");
            }

            if (_libraryPass is { } pending)
            {
                pending.Completions.Add(completion);
            }
            else
            {
                _libraryPass = new LibraryPass(progress, [completion], cancellationToken);
            }
        }

        Wake();

        // Returns as soon as the caller cancels, even while the pass waits behind another;
        // the worker then drops the pending request or cancels the running pass.
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            dropped = [.. _changedCompletions, .. _manualScans.SelectMany(scan => scan.Completions), .. _libraryPass?.Completions ?? []];
            _changedItems.Clear();
            _changedCompletions.Clear();
            _manualScans.Clear();
            _libraryPass = null;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var completion in dropped)
        {
            completion.TrySetCanceled(_stopping.Token);
        }

        if (dropped.Count > 0)
        {
            LogStoppedWithPendingRequests(_logger, dropped.Count);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => _wake.Writer.TryWrite(true);

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

    // The next pass to run, or when the pending changed items become due. Manual scans go
    // first, then the library pass, then changed items once their quiet period has elapsed.
    private (Pass? Pass, DateTimeOffset? Due) TakeNext()
    {
        lock (_lock)
        {
            if (_manualScans.Count > 0)
            {
                var scan = _manualScans[0];
                _manualScans.RemoveAt(0);
                return (new Pass(PassKind.ManualScan, scan.ItemIds, scan.Erase, NoProgress, scan.Completions, CancellationToken.None), null);
            }

            if (_libraryPass is { } library)
            {
                _libraryPass = null;
                if (library.CancellationToken.IsCancellationRequested)
                {
                    // The scheduled task was cancelled while waiting; its call has already returned.
                    foreach (var completion in library.Completions)
                    {
                        completion.TrySetCanceled(library.CancellationToken);
                    }
                }
                else
                {
                    library.Completions.AddRange(_changedCompletions);
                    _changedItems.Clear();
                    _changedCompletions.Clear();
                    return (new Pass(PassKind.Library, null, null, library.Progress, library.Completions, library.CancellationToken), null);
                }
            }

            if (_changedItems.Count == 0)
            {
                return (null, null);
            }

            var due = _lastChange + QuietPeriod;
            if (_timeProvider.GetUtcNow() < due)
            {
                return (null, due);
            }

            var changed = new Pass(PassKind.ChangedItems, [.. _changedItems], null, NoProgress, [.. _changedCompletions], CancellationToken.None);
            _changedItems.Clear();
            _changedCompletions.Clear();
            return (changed, null);
        }
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
        _passRunning = true;
        LogPassStarting(_logger, pass.Kind, pass.ItemIds?.Count ?? 0);
        try
        {
            if (pass.Erase is { } erase)
            {
                try
                {
                    await erase(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogEraseFailed(_logger, ex);
                }
            }

            await _analyzer.AnalyzeItemsAsync(pass.Progress, cancellationToken, pass.ItemIds).ConfigureAwait(false);
            foreach (var completion in pass.Completions)
            {
                completion.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogPassCancelled(_logger, pass.Kind);
            foreach (var completion in pass.Completions)
            {
                completion.TrySetCanceled(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LogPassFailed(_logger, ex, pass.Kind);
            foreach (var completion in pass.Completions)
            {
                completion.TrySetException(ex);
            }
        }
        finally
        {
            _passRunning = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting analysis pass: {Kind} ({Count} requested items)")]
    private static partial void LogPassStarting(ILogger logger, PassKind kind, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis pass cancelled: {Kind}")]
    private static partial void LogPassCancelled(ILogger logger, PassKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis pass failed: {Kind}")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, PassKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to erase the season before its manual scan; analyzing anyway")]
    private static partial void LogEraseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis queue stopped; {Count} pending requests dropped")]
    private static partial void LogStoppedWithPendingRequests(ILogger logger, int count);

    private sealed record ManualScan(Guid Key, IReadOnlyCollection<Guid> ItemIds, Func<CancellationToken, Task> Erase, List<TaskCompletionSource> Completions);

    private sealed record LibraryPass(IProgress<double> Progress, List<TaskCompletionSource> Completions, CancellationToken CancellationToken);

    private sealed record Pass(PassKind Kind, IReadOnlyCollection<Guid>? ItemIds, Func<CancellationToken, Task>? Erase, IProgress<double> Progress, List<TaskCompletionSource> Completions, CancellationToken CancellationToken);
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
