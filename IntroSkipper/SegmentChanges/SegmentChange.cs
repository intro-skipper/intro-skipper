// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Durable segment-change coordinator and retry worker. A change commits through the
/// facade's intent transaction (mutation plus journal) under the shared per-item
/// mutation stripe, is projected immediately, and when that fails or the process
/// dies is retried from the journal with exponential backoff until Jellyfin
/// converges. The journal records work, not data: applying always re-projects the
/// item's current truth through the mirror, so retries and replays can never push a
/// stale image. While mirroring is disabled the work sits durably (state
/// <see cref="ProjectionState.Skipped"/>) and replays when the toggle turns on.
/// </summary>
public sealed partial class SegmentChange : BackgroundService
{
    // Backoff after a failed attempt: BackoffBaseSeconds * 2^(attempts-1), capped at
    // an hour; the 10s poll picks work up once its due time passes.
    private const double BackoffBaseSeconds = 5;
    private const double BackoffMaxSeconds = 3600;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IIntroSkipperDatabase _database;
    private readonly ISegmentProjectionAdapter _adapter;
    private readonly IMediaSegmentMirrorPolicy _mirrorPolicy;
    private readonly SegmentMutationLocks _mutationLocks;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SegmentChange> _logger;

    // Wakes the retry loop when mirroring turns on; capacity 1 because one wake-up
    // covers any number of missed transitions. Skipped-while-disabled work never
    // carries backoff (a disabled mirror is an outcome, not a failure), so the plain
    // due pass the nudge triggers replays all of it immediately.
    private readonly SemaphoreSlim _nudge = new(0, 1);

    // Internal on purpose: the class is public only because public controllers take
    // it, and its collaborators stay internal. PluginServiceRegistrator builds it
    // through a factory since the container sees no public constructor.
    internal SegmentChange(
        IIntroSkipperDatabase database,
        ISegmentProjectionAdapter adapter,
        IMediaSegmentMirrorPolicy mirrorPolicy,
        SegmentMutationLocks mutationLocks,
        TimeProvider timeProvider,
        ILogger<SegmentChange> logger)
    {
        _database = database;
        _adapter = adapter;
        _mirrorPolicy = mirrorPolicy;
        _mutationLocks = mutationLocks;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Applies one closed segment-change intent. Once the intent is accepted the
    /// outcome reports the committed change even when the immediate projection could
    /// not run (cancellation included: the projection then reports
    /// <see cref="ProjectionState.Pending"/> and the retry worker owns the journaled
    /// work); only a failure to commit throws.
    /// </summary>
    /// <param name="intent">Closed domain intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The typed domain and projection outcome.</returns>
    public async Task<SegmentChangeOutcome> ApplyAsync(SegmentChangeIntent intent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The editor delete's Jellyfin target resolves lazily inside the facade
        // transaction, only after the in-transaction correlated lookup misses and
        // shape validation passed: one read, one decision point. The stripe held
        // here serializes that resolution with concurrent projections of the same
        // item, so a resolved target cannot go stale against another request's
        // delete: either that delete's projection already ran (the row is gone and
        // resolution reports it) or its journaled operation is still pending (the
        // facade's pending-op guard answers idempotently).
        MutationResult result;
        using (await _mutationLocks.AcquireAsync(intent.ItemId, cancellationToken).ConfigureAwait(false))
        {
            Func<Task<ExternalSegmentTarget?>>? resolveExternalTarget = intent is EditorDeleteSegmentIntent editorDelete
                ? () => _adapter.ResolveExternalTargetAsync(editorDelete.ItemId, editorDelete.SegmentId, cancellationToken)
                : null;
            result = await _database.ApplyChangeAsync(intent, resolveExternalTarget, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome is Rejected)
        {
            return result.Outcome;
        }

        if (result is { Reproject: false, Outcome: { } probeOutcome })
        {
            // A no-reproject Ignore journaled nothing (its target exists in no state
            // at all), so there is nothing to project, and force-projecting anyway
            // would let a 404-style probe run the item's unrelated pending work ahead
            // of the backoff its failure earned.
            return probeOutcome;
        }

        // Accepted and every other Ignored both project: an Ignored intent re-asserts
        // state the plugin database already holds, and re-projecting it is how a
        // diverged mirror (a ghost or missing Jellyfin row) heals on retry.
        var projected = await ProjectCommittedItemAsync(intent.ItemId, cancellationToken).ConfigureAwait(false);
        return result.Outcome ?? new Accepted(result.Affected, projected);
    }

    /// <summary>
    /// Immediately converges the given items' pending projection work, with bounded
    /// parallelism and no per-item status readback, the batch form maintenance
    /// writers use after a bulk erase. Empty and duplicate ids are ignored, an item
    /// without pending work is a cheap no-op, and anything a pass cannot finish (a
    /// failure, cancellation, disabled mirroring) stays journaled for the worker.
    /// </summary>
    /// <param name="itemIds">The items to converge.</param>
    /// <param name="cancellationToken">Cancellation token; stops the batch between items.</param>
    /// <returns>The number of items whose work applied.</returns>
    public async Task<int> ProjectItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var applied = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Plugin.Instance?.Configuration.MaxParallelism ?? 1),
            CancellationToken = cancellationToken
        };
        await Parallel.ForEachAsync(ids, options, async (itemId, token) =>
        {
            if (await ProjectItemAsync(itemId, force: true, token).ConfigureAwait(false) == ProjectionState.Applied)
            {
                Interlocked.Increment(ref applied);
            }
        }).ConfigureAwait(false);
        return applied;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _nudge.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mirrorPolicy.EnabledChanged += OnEnabledChanged;
        try
        {
            // Startup recovery: work that survived a crash or shutdown is attempted
            // once immediately, ignoring any recorded backoff. The backoff protected
            // the previous process's runtime, and restart-transient failures often
            // resolve themselves. Failures re-arm the normal backoff.
            try
            {
                foreach (var row in await _database.GetProjectionQueueAsync(stoppingToken).ConfigureAwait(false))
                {
                    await ProjectItemAsync(row.ItemId, force: true, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecoveryCycleFailed(_logger, ex);
            }

            Task? nudged = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RetryDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRecoveryCycleFailed(_logger, ex);
                }

                nudged ??= _nudge.WaitAsync(stoppingToken);
                var completed = await Task.WhenAny(Task.Delay(PollInterval, _timeProvider, stoppingToken), nudged).ConfigureAwait(false);
                if (completed == nudged)
                {
                    nudged = null;
                }

                await completed.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _mirrorPolicy.EnabledChanged -= OnEnabledChanged;
        }
    }

    private void OnEnabledChanged(object? sender, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            _nudge.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake-up already covers this transition.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race; the worker is gone and nothing needs waking.
        }
    }

    private async Task RetryDueAsync(CancellationToken cancellationToken)
    {
        if (!_mirrorPolicy.Enabled)
        {
            // Work sits durably while mirroring is off; the enable nudge replays it.
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var due = await _database.GetDueProjectionItemIdsAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var itemId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProjectItemAsync(itemId, force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies one item's pending work, if any: the journaled foreign-row deletes,
    /// then the mirror convergence, then the version-guarded completion, all under
    /// the shared mutation stripe, so a projection can never interleave with a
    /// concurrent mutation's commit-then-project sequence and push state derived from
    /// a stale read, and so <see cref="ApplyAsync"/>'s in-transaction target
    /// resolution and pending-op guard cannot race a mid-flight apply. Every step is
    /// idempotent, and the completion deletes the queue row only at the version it
    /// projected, so concurrent enqueues cannot lose work.
    /// </summary>
    private async Task<ProjectionState> ProjectItemAsync(Guid itemId, bool force, CancellationToken cancellationToken)
    {
        using var stripe = await _mutationLocks.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (await _database.ReadProjectionWorkAsync(itemId, cancellationToken).ConfigureAwait(false) is not { } work)
        {
            return ProjectionState.Applied;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (!force && work.Item.NextAttemptAt is { } due && due > now)
        {
            return ProjectionState.Pending;
        }

        try
        {
            if (!await _adapter.ApplyAsync(itemId, work.Operations, cancellationToken).ConfigureAwait(false))
            {
                // Not a failure: the work stays exactly as journaled (no backoff, no
                // attempt count, no failure text), immediately due for the enable
                // replay the nudge triggers.
                return ProjectionState.Skipped;
            }

            // Uncancelable: the apply is done; abandoning the bookkeeping would only
            // schedule a redundant (idempotent) re-sync. A completion that missed its
            // version means an unstriped analyzer or maintenance write superseded the
            // projected work mid-apply: the marker survives and the item is still
            // behind, so report Pending. Retry counts and the HTTP 202 mapping must
            // agree with the surviving marker.
            return await _database.CompleteProjectionWorkAsync(itemId, work.Item.Version, work.Operations.Select(o => o.Id).ToList(), CancellationToken.None).ConfigureAwait(false)
                ? ProjectionState.Applied
                : ProjectionState.Pending;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attempts = work.Item.AttemptCount + 1;
            var next = now + TimeSpan.FromSeconds(Math.Min(BackoffMaxSeconds, BackoffBaseSeconds * Math.Pow(2, Math.Min(10, attempts - 1))));
            await _database.RecordProjectionFailureAsync(itemId, work.Item.Version, next, Sanitize(ex), CancellationToken.None).ConfigureAwait(false);
            LogProjectionFailed(_logger, ex, itemId);
            return ProjectionState.Pending;
        }
    }

    /// <summary>
    /// The immediate projection of a committed change. Shielded: the mutation is
    /// durably committed and the work journaled, so nothing that goes wrong here
    /// (cancellation from a client disconnect, a journal read error) may surface as a
    /// failure of the accepted change. The retry loop owns the work from there.
    /// </summary>
    private async Task<ProjectionState> ProjectCommittedItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            return await ProjectItemAsync(itemId, force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProjectionState.Pending;
        }
        catch (Exception ex)
        {
            LogImmediateProjectionFailed(_logger, ex, itemId);
            return ProjectionState.Pending;
        }
    }

    private static string Sanitize(Exception exception)
    {
        var value = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1024 ? value : value[..1024];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Projection failed for item {ItemId}; the work remains pending.")]
    private static partial void LogProjectionFailed(ILogger logger, Exception exception, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Immediate projection of a committed change for item {ItemId} did not run; the retry loop owns the journaled work.")]
    private static partial void LogImmediateProjectionFailed(ILogger logger, Exception exception, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Segment projection recovery cycle failed; pending work will be retried.")]
    private static partial void LogRecoveryCycleFailed(ILogger logger, Exception exception);
}
