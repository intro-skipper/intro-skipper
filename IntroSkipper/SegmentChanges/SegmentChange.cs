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
/// mutation stripe, is projected immediately, and — when that fails or the process
/// dies — is retried from the journal with exponential backoff until Jellyfin
/// converges. The journal records work, not data: applying always re-projects the
/// item's current truth through the mirror, so retries and replays can never push a
/// stale image. While mirroring is disabled the work sits durably (state
/// <see cref="ProjectionState.Skipped"/>) and replays when the toggle turns on.
/// </summary>
internal sealed partial class SegmentChange(
    IIntroSkipperDatabase database,
    ISegmentProjectionJournal journal,
    ISegmentProjectionAdapter adapter,
    IMediaSegmentMirrorPolicy mirrorPolicy,
    SegmentMutationLocks mutationLocks,
    TimeProvider timeProvider,
    ILogger<SegmentChange> logger) : BackgroundService, ISegmentChange
{
    // Backoff after a failed attempt: BackoffBaseSeconds * 2^(attempts-1), capped at
    // an hour; the 10s poll picks work up once its due time passes.
    private const double BackoffBaseSeconds = 5;
    private const double BackoffMaxSeconds = 3600;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    // Wakes the retry loop when mirroring turns on; capacity 1 because one wake-up
    // covers any number of missed transitions. Skipped-while-disabled work never
    // carries backoff (a disabled mirror is an outcome, not a failure), so the plain
    // due pass the nudge triggers replays all of it immediately.
    private readonly SemaphoreSlim _nudge = new(0, 1);

    /// <inheritdoc />
    public async Task<SegmentChangeOutcome> ApplyAsync(SegmentChangeIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();

        // Resolution runs under the item's mutation stripe, like the mutation it
        // feeds: projections take the same stripe, so a target resolved here cannot
        // go stale against a concurrent request's delete of the same row — either
        // that delete's projection already ran (the row is gone and resolution
        // reports it) or its journaled operation is still pending (the facade's
        // pending-op guard answers idempotently). The editor delete resolves only
        // when no plugin row owns the id: a correlated dispatch is decided
        // authoritatively and must not depend on a Jellyfin read that may fail
        // while the mirror lags.
        MutationResult result;
        using (await mutationLocks.AcquireAsync(intent.ItemId, cancellationToken).ConfigureAwait(false))
        {
            ExternalSegmentTarget? externalTarget = null;
            if (intent is DeleteExternalSegmentIntent external && external.ExternalSegmentId != Guid.Empty)
            {
                externalTarget = await adapter.ResolveExternalTargetAsync(external.ItemId, external.ExternalSegmentId, cancellationToken).ConfigureAwait(false);
            }
            else if (intent is EditorDeleteSegmentIntent editorDelete && editorDelete.SegmentId != Guid.Empty)
            {
                var ownRow = await database.GetSegmentAsync(editorDelete.SegmentId, cancellationToken).ConfigureAwait(false);
                if (ownRow is null || ownRow.ItemId != editorDelete.ItemId)
                {
                    externalTarget = await adapter.ResolveExternalTargetAsync(editorDelete.ItemId, editorDelete.SegmentId, cancellationToken).ConfigureAwait(false);
                }
            }

            result = await database.ApplyChangeAsync(intent, externalTarget, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome is Rejected)
        {
            return result.Outcome;
        }

        // Accepted and Ignored both project: an Ignored intent re-asserts state the
        // plugin database already holds, and re-projecting it is how a diverged
        // mirror (a ghost or missing Jellyfin row) heals on retry.
        var projected = await ProjectCommittedItemAsync(intent.ItemId, cancellationToken).ConfigureAwait(false);
        return result.Outcome ?? new Accepted(result.Affected, projected);
    }

    /// <inheritdoc />
    public async Task<ProjectionStatus> GetProjectionStatusAsync(ProjectionScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var rows = await journal.GetProjectionQueueAsync(scope.ItemId, cancellationToken).ConfigureAwait(false);
        var pendingState = mirrorPolicy.Enabled ? ProjectionState.Pending : ProjectionState.Skipped;
        var items = rows.Select(row => new ItemProjectionStatus(row.ItemId, pendingState, row.AttemptCount, row.NextAttemptAt, row.Failure)).ToList();

        // Applied items hold no queue row, so the all-items scope lists only pending
        // work; a one-item scope still answers explicitly.
        if (scope.ItemId is { } itemId && items.Count == 0)
        {
            items.Add(new ItemProjectionStatus(itemId, ProjectionState.Applied, 0, null, null));
        }

        return new ProjectionStatus(scope, items);
    }

    /// <inheritdoc />
    public async Task<ProjectionRetryOutcome> RetryProjectionAsync(ProjectionScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var rows = await journal.GetProjectionQueueAsync(scope.ItemId, cancellationToken).ConfigureAwait(false);
        var applied = 0;
        foreach (var row in rows)
        {
            if (await ProjectItemAsync(row.ItemId, force: true, cancellationToken).ConfigureAwait(false) == ProjectionState.Applied)
            {
                applied++;
            }
        }

        return new ProjectionRetryOutcome(scope, applied, await GetProjectionStatusAsync(scope, cancellationToken).ConfigureAwait(false));
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
        mirrorPolicy.EnabledChanged += OnEnabledChanged;
        try
        {
            // Startup recovery: work that survived a crash or shutdown is attempted
            // once immediately, ignoring any recorded backoff — the backoff protected
            // the previous process's runtime, and restart-transient failures often
            // resolve themselves. Failures re-arm the normal backoff.
            try
            {
                await RetryProjectionAsync(ProjectionScope.All, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecoveryCycleFailed(logger, ex);
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
                    LogRecoveryCycleFailed(logger, ex);
                }

                nudged ??= _nudge.WaitAsync(stoppingToken);
                var completed = await Task.WhenAny(Task.Delay(PollInterval, timeProvider, stoppingToken), nudged).ConfigureAwait(false);
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
            mirrorPolicy.EnabledChanged -= OnEnabledChanged;
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
        if (!mirrorPolicy.Enabled)
        {
            // Work sits durably while mirroring is off; the enable nudge replays it.
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var due = await journal.GetDueProjectionItemIdsAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var itemId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProjectItemAsync(itemId, force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies one item's pending work, if any: the journaled foreign-row deletes,
    /// then the mirror convergence, then the version-guarded completion — all under
    /// the shared mutation stripe, for the same reason the editor holds it across its
    /// write-plus-sync: a projection interleaving between another mutation's write and
    /// its rollback would bake the rolled-back state into the mirror. Every step is
    /// idempotent, and the completion deletes the queue row only at the version it
    /// projected, so concurrent enqueues cannot lose work.
    /// </summary>
    private async Task<ProjectionState> ProjectItemAsync(Guid itemId, bool force, CancellationToken cancellationToken)
    {
        using var stripe = await mutationLocks.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        var work = await journal.ReadProjectionWorkAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (work is null)
        {
            return ProjectionState.Applied;
        }

        if (!mirrorPolicy.Enabled)
        {
            return ProjectionState.Skipped;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!force && work.Item.NextAttemptAt is { } due && due > now)
        {
            return ProjectionState.Pending;
        }

        try
        {
            var operations = work.Operations.Select(o => new ProjectedExternalOperation(o.ExternalSegmentId, o.ExpectedType, o.StartTicks, o.EndTicks)).ToList();
            if (await adapter.ApplyAsync(itemId, operations, cancellationToken).ConfigureAwait(false) == ProjectionApplyOutcome.MirroringDisabled)
            {
                // Not a failure: the work stays exactly as journaled — no backoff, no
                // attempt count, no failure text — immediately due for the enable
                // replay the nudge triggers.
                return ProjectionState.Skipped;
            }

            // Uncancelable: the apply is done; abandoning the bookkeeping would only
            // schedule a redundant (idempotent) re-sync.
            await journal.CompleteProjectionWorkAsync(itemId, work.Item.Version, work.Operations.Select(o => o.Id).ToList(), CancellationToken.None).ConfigureAwait(false);
            return ProjectionState.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attempts = work.Item.AttemptCount + 1;
            var next = now + TimeSpan.FromSeconds(Math.Min(BackoffMaxSeconds, BackoffBaseSeconds * Math.Pow(2, Math.Min(10, attempts - 1))));
            await journal.RecordProjectionFailureAsync(itemId, next, Sanitize(ex), CancellationToken.None).ConfigureAwait(false);
            LogProjectionFailed(logger, ex, itemId);
            return ProjectionState.Pending;
        }
    }

    /// <summary>
    /// The immediate projection of a committed change. Shielded: the mutation is
    /// durably committed and the work journaled, so nothing that goes wrong here —
    /// cancellation (a client disconnect), a journal read error — may surface as a
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
            LogImmediateProjectionFailed(logger, ex, itemId);
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
