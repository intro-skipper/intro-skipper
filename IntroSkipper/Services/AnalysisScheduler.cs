// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading.Channels;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
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
/// nothing absorbs it, a repeated key merges into the pending scan, and the season is
/// resolved when the pass starts, not when the scan was requested. A library pass (from the
/// scheduled task) starts once the worker is free and every pending manual scan has run; it
/// absorbs the pending changed items, which complete with it, and puts them back if it is
/// cancelled. A pass waits for the pass in flight; only the scheduled task's own token or
/// shutdown cancels a running pass. Requests arriving during a pass wait for the next one.
/// Pending requests are dropped on shutdown.
/// </remarks>
/// <param name="analyzer">Analyzer run by every pass.</param>
/// <param name="seasonResolver">Resolver of the season a manual scan erases and analyzes.</param>
/// <param name="timeProvider">Clock for the quiet period.</param>
/// <param name="logger">Logger.</param>
public sealed partial class AnalysisScheduler(
    BaseItemAnalyzerTask analyzer,
    SeasonResolver seasonResolver,
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
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AnalysisScheduler> _logger = logger;
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _changedItems = [];
    private readonly List<TaskCompletionSource> _changedCompletions = [];
    private readonly List<ManualScan> _manualScans = [];
    private readonly List<LibraryCaller> _libraryCallers = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stopping = new();
    private DateTimeOffset _lastChange;
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
                return new AnalysisSchedulerStatus(_passRunning, _changedItems.Count, _manualScans.Count, _libraryCallers.Count > 0);
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
    /// Queues a manual scan: its own pass that resolves the season the dashboard shows
    /// under the key, erases it, then analyzes it, ahead of any pending library pass. An
    /// erase failure is logged and the analysis still runs. A scan for a key that is
    /// already pending merges into it. A key that no longer resolves to a season of the
    /// series when the pass starts completes without running.
    /// </summary>
    /// <param name="seriesId">The series the season is requested under.</param>
    /// <param name="key">The season key the scan is requested for.</param>
    /// <param name="erase">Erases the season's timestamps and cache; runs on the worker before the analysis.</param>
    /// <returns>A task that completes with the scan's pass.</returns>
    internal Task ScanAsync(Guid seriesId, Guid key, Func<DisplayedSeason, CancellationToken, Task> erase)
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
                _manualScans.Add(new ManualScan(seriesId, key, erase, [completion]));
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
    /// <param name="cancellationToken">Cancels this request only: withdraws it while it waits, or cancels the pass once it runs. Other requests stay queued.</param>
    /// <returns>A task that completes when the pass has run.</returns>
    /// <exception cref="OperationCanceledException">The request was cancelled, or the queue has stopped.</exception>
    public async Task RunLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var caller = new LibraryCaller(progress, NewCompletion(), cancellationToken);
        lock (_lock)
        {
            if (_stopped)
            {
                throw new OperationCanceledException("The analysis queue has stopped.");
            }

            _libraryCallers.Add(caller);
        }

        Wake();

        // A caller that cancels while waiting withdraws its request before returning, so
        // a later request never inherits a cancelled one; a pass already running sees the
        // token through its linked source. Either way the caller returns at once.
        try
        {
            await caller.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Withdraw(caller);
            throw;
        }
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
            dropped = [.. _changedCompletions, .. _manualScans.SelectMany(scan => scan.Completions), .. _libraryCallers.Select(caller => caller.Completion)];
            _changedItems.Clear();
            _changedCompletions.Clear();
            _manualScans.Clear();
            _libraryCallers.Clear();
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

    private void Withdraw(LibraryCaller caller)
    {
        lock (_lock)
        {
            _libraryCallers.Remove(caller);
        }

        caller.Completion.TrySetCanceled(caller.CancellationToken);
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
                return (new Pass(PassKind.ManualScan, scan, null, NoProgress, scan.Completions, null, []), null);
            }

            if (_libraryCallers.Count > 0)
            {
                var callers = _libraryCallers.ToArray();
                _libraryCallers.Clear();
                var absorbed = new Absorbed([.. _changedItems], [.. _changedCompletions]);
                _changedItems.Clear();
                _changedCompletions.Clear();
                IProgress<double> progress = callers.Length == 1 ? callers[0].Progress : new ForwardingProgress([.. callers.Select(caller => caller.Progress)]);
                return (new Pass(PassKind.Library, null, null, progress, [.. callers.Select(caller => caller.Completion)], absorbed, [.. callers.Select(caller => caller.CancellationToken)]), null);
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

            var changed = new Pass(PassKind.ChangedItems, null, [.. _changedItems], NoProgress, [.. _changedCompletions], null, []);
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource([stoppingToken, .. pass.CancellationTokens]);
        var cancellationToken = linked.Token;
        _passRunning = true;
        LogPassStarting(_logger, pass.Kind);
        try
        {
            if (pass.Scan is { } scan)
            {
                await RunScanAsync(scan, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _analyzer.AnalyzeItemsAsync(pass.Progress, cancellationToken, pass.ItemIds).ConfigureAwait(false);
            }

            Settle(pass.Completions, completion => completion.TrySetResult());
            Settle(pass.Absorbed?.Completions, completion => completion.TrySetResult());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogPassCancelled(_logger, pass.Kind);
            Settle(pass.Completions, completion => completion.TrySetCanceled(cancellationToken));
            Requeue(pass.Absorbed);
        }
        catch (Exception ex)
        {
            LogPassFailed(_logger, ex, pass.Kind);
            Settle(pass.Completions, completion => completion.TrySetException(ex));
            Settle(pass.Absorbed?.Completions, completion => completion.TrySetException(ex));
        }
        finally
        {
            _passRunning = false;
        }
    }

    // The season is resolved now, not when the scan was requested, so a season that
    // changed while the scan waited is erased and analyzed as it is.
    private async Task RunScanAsync(ManualScan scan, CancellationToken cancellationToken)
    {
        if (_seasonResolver.ResolveDisplayed(scan.Key) is not { } season || season.SeriesId != scan.SeriesId)
        {
            LogScanSeasonGone(_logger, scan.Key);
            return;
        }

        try
        {
            await scan.Erase(season, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEraseFailed(_logger, ex, scan.Key);
        }

        await _analyzer.AnalyzeItemsAsync(NoProgress, cancellationToken, season.Episodes.Select(episode => episode.EpisodeId).ToHashSet()).ConfigureAwait(false);
    }

    // Changed items a cancelled library pass absorbed go back to the changed set, so a
    // cancelled scheduled task never loses them; their quiet period is still measured
    // from the change, so they run next.
    private void Requeue(Absorbed? absorbed)
    {
        if (absorbed is null || absorbed.Completions.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_stopped)
            {
                _changedItems.UnionWith(absorbed.Items);
                _changedCompletions.AddRange(absorbed.Completions);
                Wake();
                return;
            }
        }

        Settle(absorbed.Completions, completion => completion.TrySetCanceled(_stopping.Token));
    }

    private static void Settle(List<TaskCompletionSource>? completions, Action<TaskCompletionSource> settle)
    {
        foreach (var completion in completions ?? [])
        {
            settle(completion);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting analysis pass: {Kind}")]
    private static partial void LogPassStarting(ILogger logger, PassKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis pass cancelled: {Kind}")]
    private static partial void LogPassCancelled(ILogger logger, PassKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis pass failed: {Kind}")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, PassKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to erase season {Key} before its manual scan; analyzing anyway")]
    private static partial void LogEraseFailed(ILogger logger, Exception exception, Guid key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping the manual scan of {Key}: it no longer resolves to a season of the requested series")]
    private static partial void LogScanSeasonGone(ILogger logger, Guid key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis queue stopped; {Count} pending requests dropped")]
    private static partial void LogStoppedWithPendingRequests(ILogger logger, int count);

    private sealed record ManualScan(Guid SeriesId, Guid Key, Func<DisplayedSeason, CancellationToken, Task> Erase, List<TaskCompletionSource> Completions);

    private sealed record LibraryCaller(IProgress<double> Progress, TaskCompletionSource Completion, CancellationToken CancellationToken);

    private sealed record Absorbed(IReadOnlyCollection<Guid> Items, List<TaskCompletionSource> Completions);

    private sealed record Pass(
        PassKind Kind,
        ManualScan? Scan,
        IReadOnlyCollection<Guid>? ItemIds,
        IProgress<double> Progress,
        List<TaskCompletionSource> Completions,
        Absorbed? Absorbed,
        IReadOnlyList<CancellationToken> CancellationTokens);

    private sealed class ForwardingProgress(IReadOnlyList<IProgress<double>> targets) : IProgress<double>
    {
        public void Report(double value)
        {
            foreach (var target in targets)
            {
                target.Report(value);
            }
        }
    }
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
