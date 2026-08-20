// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.SegmentChanges;

/// <summary>EF-backed durable segment change coordinator and retry worker.</summary>
internal sealed partial class SegmentChange(
    IDbContextFactory<IntroSkipperDbContext> contextFactory,
    IIntroSkipperDatabase database,
    ISegmentProjectionAdapter adapter,
    ISegmentProjectionConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<SegmentChange> logger) : BackgroundService, ISegmentChange
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly StripedAsyncLock _locks = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private int _reconcileRequested;

    /// <inheritdoc />
    public async Task<SegmentChangeOutcome> ApplyAsync(SegmentChangeIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (intent.ItemId == Guid.Empty)
        {
            return new Rejected(SegmentChangeRejectedReason.EmptyItemId, "Item ID must not be empty.");
        }

        ExternalSegmentTarget? externalTarget = null;
        if (intent is DeleteExternalSegmentIntent external)
        {
            externalTarget = await adapter.ResolveExternalTargetAsync(external.ItemId, external.ExternalSegmentId, cancellationToken).ConfigureAwait(false);

            if (externalTarget is null)
            {
                return new Rejected(SegmentChangeRejectedReason.ExternalSegmentNotFound, "External segment was not found.");
            }

            if (externalTarget.ItemId != external.ItemId)
            {
                return new Rejected(SegmentChangeRejectedReason.ExternalItemMismatch, "External segment belongs to another item.");
            }

            if (externalTarget.Type != external.ExpectedType)
            {
                return new Rejected(SegmentChangeRejectedReason.ExternalTypeMismatch, "External segment type does not match the expected type.");
            }
        }

        if (Validate(intent) is { } rejection)
        {
            return rejection;
        }

        await database.InitializeAsync().ConfigureAwait(false);
        var changeId = Guid.CreateVersion7();
        IReadOnlyList<SegmentValue> affected;
        {
            using var itemLock = await _locks.AcquireAsync(intent.ItemId, cancellationToken).ConfigureAwait(false);
            using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var mutation = await MutateAsync(db, intent, externalTarget, cancellationToken).ConfigureAwait(false);
                    if (mutation.Outcome is not null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return mutation.Outcome;
                    }

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    affected = mutation.Affected;
                    await AddPlanAsync(db, changeId, intent.ItemId, mutation.ExternalOperations, cancellationToken).ConfigureAwait(false);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var projected = await ProjectAcceptedChangeAsync(intent.ItemId, changeId).ConfigureAwait(false);
        return new Accepted(changeId, affected, [new SegmentProjectionResult(intent.ItemId, projected)]);
    }

    /// <inheritdoc />
    public async Task<ProjectionStatus> GetProjectionStatusAsync(ProjectionScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await database.InitializeAsync().ConfigureAwait(false);
        using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var heads = db.ProjectionHeads.AsNoTracking();
        if (scope.ItemId.HasValue)
        {
            heads = heads.Where(head => head.ItemId == scope.ItemId.Value);
        }

        var values = await heads.OrderBy(head => head.ItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ItemProjectionStatus>(values.Count);
        foreach (var head in values)
        {
            var attempt = await db.ProjectionAttempts.AsNoTracking()
                .Where(value => value.ItemId == head.ItemId)
                .OrderBy(value => value.NextAttemptAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            results.Add(new ItemProjectionStatus(
                head.ItemId,
                head.LastAcceptedSequence,
                head.LastAppliedSequence,
                head.Status,
                attempt?.AttemptCount ?? 0,
                attempt?.NextAttemptAt,
                attempt?.Failure));
        }

        return new ProjectionStatus(scope, results);
    }

    /// <inheritdoc />
    public async Task<ProjectionRetryOutcome> RetryProjectionAsync(ProjectionScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await database.InitializeAsync().ConfigureAwait(false);
        IReadOnlyList<Guid> itemIds;
        if (scope.ItemId.HasValue)
        {
            itemIds = [scope.ItemId.Value];
        }
        else
        {
            using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            itemIds = await db.ProjectionPlans.AsNoTracking().Select(plan => plan.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var applied = 0;
        foreach (var id in itemIds)
        {
            while (await ProjectItemAsync(id, force: true, cancellationToken).ConfigureAwait(false) is ProjectionState.Applied or ProjectionState.Skipped)
            {
                applied++;
                using var verify = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (!await verify.ProjectionPlans.AnyAsync(plan => plan.ItemId == id, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }

        return new ProjectionRetryOutcome(scope, applied, await GetProjectionStatusAsync(scope, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        configuration.EnabledChanged += OnEnabledChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _reconcileLock.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                await RetryProjectionAsync(ProjectionScope.All, stoppingToken).ConfigureAwait(false);
                await RetryDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecoveryCycleFailed(logger, ex);
            }

            using var timer = new PeriodicTimer(PollInterval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RetryDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRecoveryCycleFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            configuration.EnabledChanged -= OnEnabledChanged;
        }
    }

    private void OnEnabledChanged(object? sender, bool enabled)
    {
        if (enabled)
        {
            Interlocked.Exchange(ref _reconcileRequested, 1);
            _ = ReconcileAfterEnableAsync();
        }
    }

    private async Task ReconcileAfterEnableAsync()
    {
        await _reconcileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _reconcileRequested) == 0 || !configuration.Enabled)
            {
                return;
            }

            await database.InitializeAsync().ConfigureAwait(false);
            using var db = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var itemIds = await db.Segments.Select(value => value.ItemId)
                .Union(db.DisabledItems.Select(value => value.ItemId))
                .Union(db.AnalyzedStates.Select(value => value.ItemId))
                .Union(db.ProjectionHeads.Select(value => value.ItemId))
                .Union(db.ProjectionExternalOperations.Select(value => value.ItemId))
                .Distinct()
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var itemId in itemIds)
            {
                {
                    using var itemLock = await _locks.AcquireAsync(itemId, CancellationToken.None).ConfigureAwait(false);
                    using var writeDb = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                    var transaction = await writeDb.Database.BeginTransactionAsync().ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        var retained = await writeDb.ProjectionExternalOperations
                            .Where(operation => operation.ItemId == itemId)
                            .OrderBy(operation => operation.Sequence).ThenBy(operation => operation.Position)
                            .ToListAsync().ConfigureAwait(false);
                        var operations = retained.Select(value => new ProjectedExternalOperation(value.ExternalSegmentId, value.ExpectedType, value.Kind)).ToList();
                        await AddPlanAsync(writeDb, Guid.CreateVersion7(), itemId, operations, CancellationToken.None).ConfigureAwait(false);
                        writeDb.ProjectionExternalOperations.RemoveRange(retained);
                        await writeDb.SaveChangesAsync().ConfigureAwait(false);
                        await transaction.CommitAsync().ConfigureAwait(false);
                    }
                }

                await ProjectItemAsync(itemId, force: true, CancellationToken.None).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _reconcileRequested, 0);
        }
        catch (Exception ex)
        {
            LogReconciliationFailed(logger, ex);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task RetryDueAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync().ConfigureAwait(false);
        if (configuration.Enabled && Volatile.Read(ref _reconcileRequested) != 0)
        {
            await ReconcileAfterEnableAsync().ConfigureAwait(false);
        }

        using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.Enabled
            && Volatile.Read(ref _reconcileRequested) == 0
            && (await db.ProjectionHeads.AnyAsync(head => head.Status == ProjectionState.Skipped, cancellationToken).ConfigureAwait(false)
                || await db.ProjectionExternalOperations.AnyAsync(
                    operation => !db.ProjectionPlans.Any(
                        plan => plan.ChangeId == operation.ChangeId && plan.ItemId == operation.ItemId),
                    cancellationToken).ConfigureAwait(false)))
        {
            Interlocked.Exchange(ref _reconcileRequested, 1);
            await ReconcileAfterEnableAsync().ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var itemIds = await db.ProjectionPlans.AsNoTracking()
            .Where(plan => !db.ProjectionAttempts.Any(attempt => attempt.ChangeId == plan.ChangeId && attempt.ItemId == plan.ItemId && attempt.NextAttemptAt > now))
            .Select(plan => plan.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(itemIds.Select(id => ProjectItemAsync(id, force: false, cancellationToken))).ConfigureAwait(false);
    }

    private async Task<ProjectionState> ProjectAcceptedChangeAsync(Guid itemId, Guid changeId)
    {
        while (true)
        {
            using var db = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var target = await db.ProjectionPlans.AsNoTracking()
                .FirstOrDefaultAsync(plan => plan.ChangeId == changeId && plan.ItemId == itemId)
                .ConfigureAwait(false);
            if (target is null)
            {
                var head = await db.ProjectionHeads.AsNoTracking().FirstAsync(value => value.ItemId == itemId).ConfigureAwait(false);
                return head.Status == ProjectionState.Skipped ? ProjectionState.Skipped : ProjectionState.Applied;
            }

            var state = await ProjectItemAsync(itemId, force: true, CancellationToken.None).ConfigureAwait(false);
            if (state == ProjectionState.Pending)
            {
                return state;
            }

            using var verify = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            if (!await verify.ProjectionPlans.AnyAsync(plan => plan.ChangeId == changeId && plan.ItemId == itemId).ConfigureAwait(false))
            {
                return state;
            }
        }
    }

    private async Task<ProjectionState> ProjectItemAsync(Guid itemId, bool force, CancellationToken cancellationToken)
    {
        using var itemLock = await _locks.AcquireAsync(itemId, cancellationToken).ConfigureAwait(false);
        using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var planRow = await db.ProjectionPlans.AsNoTracking().Where(plan => plan.ItemId == itemId)
            .OrderBy(plan => plan.Sequence).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (planRow is null)
        {
            var head = await db.ProjectionHeads.AsNoTracking().FirstOrDefaultAsync(value => value.ItemId == itemId, cancellationToken).ConfigureAwait(false);
            return head?.Status ?? ProjectionState.Applied;
        }

        var attempt = await db.ProjectionAttempts.FirstAsync(value => value.ChangeId == planRow.ChangeId && value.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!force && attempt.NextAttemptAt > now)
        {
            return ProjectionState.Pending;
        }

        if (!configuration.Enabled)
        {
            await CompactAsync(db, planRow, ProjectionState.Skipped, retainExternalOperations: true, cancellationToken).ConfigureAwait(false);
            return ProjectionState.Skipped;
        }

        var segments = await db.ProjectionPlanSegments.AsNoTracking()
            .Where(value => value.ChangeId == planRow.ChangeId && value.ItemId == itemId)
            .OrderBy(value => value.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
        var operations = await db.ProjectionExternalOperations.AsNoTracking()
            .Where(value => value.ChangeId == planRow.ChangeId && value.ItemId == itemId)
            .OrderBy(value => value.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = new SegmentProjectionPlan(
            planRow.ChangeId,
            itemId,
            planRow.Sequence,
            segments.Select(value => new ProjectedSegment(value.SegmentId, value.Type, value.StartTicks, value.EndTicks, value.Source)).ToList(),
            operations.Select(value => new ProjectedExternalOperation(value.ExternalSegmentId, value.ExpectedType, value.Kind)).ToList());

        try
        {
            await adapter.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
            await CompactAsync(db, planRow, ProjectionState.Applied, retainExternalOperations: false, cancellationToken).ConfigureAwait(false);
            return ProjectionState.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            attempt.AttemptCount++;
            attempt.LastAttemptAt = now;
            attempt.NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(10, attempt.AttemptCount - 1))));
            attempt.Failure = Sanitize(ex);
            attempt.Status = ProjectionState.Pending;
            var head = await db.ProjectionHeads.FirstAsync(value => value.ItemId == itemId, cancellationToken).ConfigureAwait(false);
            head.Status = ProjectionState.Pending;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogProjectionFailed(logger, ex, itemId, planRow.Sequence);
            return ProjectionState.Pending;
        }
    }

    private static async Task CompactAsync(IntroSkipperDbContext db, DbProjectionPlan plan, ProjectionState state, bool retainExternalOperations, CancellationToken cancellationToken)
    {
        var head = await db.ProjectionHeads.FirstAsync(value => value.ItemId == plan.ItemId, cancellationToken).ConfigureAwait(false);
        if (state == ProjectionState.Applied)
        {
            head.LastAppliedSequence = plan.Sequence;
        }

        head.Status = await db.ProjectionPlans.AnyAsync(
            value => value.ItemId == plan.ItemId && (value.ChangeId != plan.ChangeId || value.ItemId != plan.ItemId),
            cancellationToken).ConfigureAwait(false)
            ? ProjectionState.Pending
            : state;
        await db.ProjectionPlanSegments.Where(value => value.ChangeId == plan.ChangeId && value.ItemId == plan.ItemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (!retainExternalOperations)
        {
            await db.ProjectionExternalOperations.Where(value => value.ChangeId == plan.ChangeId && value.ItemId == plan.ItemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await db.ProjectionAttempts.Where(value => value.ChangeId == plan.ChangeId && value.ItemId == plan.ItemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.ProjectionPlans.Where(value => value.ChangeId == plan.ChangeId && value.ItemId == plan.ItemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AddPlanAsync(IntroSkipperDbContext db, Guid changeId, Guid itemId, IReadOnlyList<ProjectedExternalOperation> externalOperations, CancellationToken cancellationToken)
    {
        var head = await db.ProjectionHeads.FirstOrDefaultAsync(value => value.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        var sequence = head?.LastAcceptedSequence + 1 ?? 1;
        if (head is null)
        {
            head = new DbProjectionHead { ItemId = itemId };
            db.ProjectionHeads.Add(head);
        }

        head.LastAcceptedSequence = sequence;
        head.Status = ProjectionState.Pending;
        db.ProjectionPlans.Add(new DbProjectionPlan { ChangeId = changeId, ItemId = itemId, Sequence = sequence, CreatedAt = timeProvider.GetUtcNow().UtcDateTime });
        db.ProjectionAttempts.Add(new DbProjectionAttempt { ChangeId = changeId, ItemId = itemId, Status = ProjectionState.Pending, NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime });

        var disabled = await db.DisabledItems.AnyAsync(value => value.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        var image = await db.Segments.Where(value => value.ItemId == itemId && value.State == SegmentState.Active && (!disabled || value.Source == SegmentSource.User))
            .OrderBy(value => value.Type).ThenBy(value => value.StartTicks).ThenBy(value => value.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.ProjectionPlanSegments.AddRange(image.Select((value, index) => new DbProjectionPlanSegment
        {
            ChangeId = changeId,
            ItemId = itemId,
            Position = index,
            SegmentId = value.Id,
            Type = AnalysisHelpers.ModeToSegmentType[value.Type],
            StartTicks = value.StartTicks,
            EndTicks = value.EndTicks,
            Source = value.Source
        }));
        db.ProjectionExternalOperations.AddRange(externalOperations.Select((externalOperation, index) => new DbProjectionExternalOperation
            {
                ChangeId = changeId,
                ItemId = itemId,
                Sequence = sequence,
                Position = index,
                ExternalSegmentId = externalOperation.ExternalSegmentId,
                ExpectedType = externalOperation.ExpectedType,
                Kind = externalOperation.Kind
            }));
    }

    private static Rejected? Validate(SegmentChangeIntent intent)
    {
        static bool ValidMode(AnalysisMode mode) => AnalysisHelpers.IsSupported(mode);
        static bool ValidRange(long start, long end) => start >= 0 && end > start;

        return intent switch
        {
            AddUserSegmentIntent value when !ValidMode(value.Mode) || !ValidRange(value.StartTicks, value.EndTicks) => new(SegmentChangeRejectedReason.InvalidModeOrRange, "Invalid mode or tick range."),
            ReplaceUserSegmentsForModeIntent value when value.Segments is null || !ValidMode(value.Mode) || value.Segments.Any(range => !ValidRange(range.StartTicks, range.EndTicks)) => new(SegmentChangeRejectedReason.InvalidModeOrRange, "Invalid mode or tick range."),
            UpdateSegmentIntent value when value.SegmentId == Guid.Empty || !ValidRange(value.StartTicks, value.EndTicks) => new(SegmentChangeRejectedReason.InvalidSegmentIdOrRange, "Invalid segment ID or tick range."),
            DeleteSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            RestoreSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            DeleteExternalSegmentIntent value when value.ExternalSegmentId == Guid.Empty || AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType) is null => new(SegmentChangeRejectedReason.InvalidExternalIdOrType, "Invalid external segment ID or type."),
            WriteUserTimestampsIntent value when value.Timestamps is null || value.Timestamps.Count == 0 || value.Timestamps.Any(timestamp => !ValidMode(timestamp.Mode) || !ValidRange(timestamp.StartTicks, timestamp.EndTicks)) || value.Timestamps.Select(timestamp => timestamp.Mode).Distinct().Count() != value.Timestamps.Count => new(SegmentChangeRejectedReason.InvalidUserTimestamps, "User timestamps must contain unique supported modes and valid ranges."),
            SegmentVisibilityChangeIntent value when value.SeasonId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySeasonId, "Season ID must not be empty."),
            _ => null
        };
    }

    private static async Task<MutationResult> MutateAsync(IntroSkipperDbContext db, SegmentChangeIntent intent, ExternalSegmentTarget? externalTarget, CancellationToken cancellationToken)
    {
        var rows = await db.Segments.Where(value => value.ItemId == intent.ItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var affected = new List<SegmentValue>();
        var externalOperations = new List<ProjectedExternalOperation>();

        switch (intent)
        {
            case AddUserSegmentIntent value:
                {
                    var exact = rows.FirstOrDefault(row => row.Type == value.Mode && row.StartTicks == value.StartTicks && row.EndTicks == value.EndTicks);
                    if (exact is { Source: SegmentSource.User, State: SegmentState.Active })
                    {
                        if (await HasAnalyzedStateAsync(db, value.ItemId, value.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false))
                        {
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.UserSegmentAlreadyExists, "The user segment already exists.");
                        }

                        affected.Add(ToValue(exact));
                        await UpsertAnalyzedStateAsync(db, value.ItemId, value.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    exact ??= new DbSegment(value.ItemId, value.Mode, value.StartTicks, value.EndTicks, SegmentSource.User);
                    if (exact.Id == Guid.Empty || !rows.Contains(exact))
                    {
                        db.Segments.Add(exact);
                    }

                    exact.Source = SegmentSource.User;
                    exact.State = SegmentState.Active;
                    affected.Add(ToValue(exact));
                    await UpsertAnalyzedStateAsync(db, value.ItemId, value.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case ReplaceUserSegmentsForModeIntent value:
                {
                    var requested = value.Segments.Distinct().OrderBy(range => range.StartTicks).ThenBy(range => range.EndTicks).ToList();
                    var active = rows.Where(row => row.Type == value.Mode && row.State == SegmentState.Active).ToList();
                    if (active.Count == requested.Count && active.All(row => row.Source == SegmentSource.User && requested.Contains(new SegmentRange(row.StartTicks, row.EndTicks))))
                    {
                        if (await HasAnalyzedStateAsync(db, value.ItemId, value.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false))
                        {
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.UserImageAlreadyExists, "The requested user image already exists.");
                        }

                        affected.AddRange(active.Select(ToValue));
                        await UpsertAnalyzedStateAsync(db, value.ItemId, value.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    db.Segments.RemoveRange(active);
                    foreach (var range in requested)
                    {
                        var tombstone = rows.FirstOrDefault(row => row.Type == value.Mode && row.State == SegmentState.Suppressed && row.StartTicks == range.StartTicks && row.EndTicks == range.EndTicks);
                        var row = tombstone ?? new DbSegment(value.ItemId, value.Mode, range.StartTicks, range.EndTicks, SegmentSource.User);
                        row.State = SegmentState.Active;
                        row.Source = SegmentSource.User;
                        if (tombstone is null)
                        {
                            db.Segments.Add(row);
                        }

                        affected.Add(ToValue(row));
                    }

                    if (requested.Count > 0)
                    {
                        await UpsertAnalyzedStateAsync(db, value.ItemId, value.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await DeriveAnalyzedStateAsync(db, rows, value.ItemId, value.Mode, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

            case UpdateSegmentIntent value:
                {
                    var row = rows.FirstOrDefault(item => item.Id == value.SegmentId && item.State == SegmentState.Active);
                    if (row is null)
                    {
                        return MutationResult.Reject(SegmentChangeRejectedReason.SegmentMissingOrSuppressed, "Segment was not found on the item or is suppressed.");
                    }

                    if (row.StartTicks == value.StartTicks && row.EndTicks == value.EndTicks && row.Source == SegmentSource.User)
                    {
                        if (await HasAnalyzedStateAsync(db, value.ItemId, row.Type, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false))
                        {
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentAlreadyHasValues, "The segment already has the requested values.");
                        }

                        affected.Add(ToValue(row));
                        await UpsertAnalyzedStateAsync(db, value.ItemId, row.Type, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    var occupant = rows.FirstOrDefault(item => item.Id != row.Id && item.Type == row.Type && item.StartTicks == value.StartTicks && item.EndTicks == value.EndTicks);
                    if (occupant is { State: SegmentState.Active })
                    {
                        db.Segments.Remove(row);
                        occupant.Source = SegmentSource.User;
                        affected.Add(ToValue(occupant));
                    }
                    else
                    {
                        if (occupant is not null)
                        {
                            db.Segments.Remove(occupant);
                        }

                        row.StartTicks = value.StartTicks;
                        row.EndTicks = value.EndTicks;
                        row.Source = SegmentSource.User;
                        affected.Add(ToValue(row));
                    }

                    await UpsertAnalyzedStateAsync(db, value.ItemId, row.Type, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case DeleteSegmentIntent value:
                {
                    var row = rows.FirstOrDefault(item => item.Id == value.SegmentId && item.State == SegmentState.Active);
                    if (row is null)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "Segment was not found on the item or was already deleted.");
                    }

                    if (row.Source == SegmentSource.User)
                    {
                        db.Segments.Remove(row);
                    }
                    else
                    {
                        row.State = SegmentState.Suppressed;
                    }

                    affected.Add(ToValue(row));
                    await DeriveAnalyzedStateAsync(db, rows, value.ItemId, row.Type, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case RestoreSegmentIntent value:
                {
                    var row = rows.FirstOrDefault(item => item.Id == value.SegmentId && item.State == SegmentState.Suppressed);
                    if (row is null)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrNotSuppressed, "Segment was not found on the item or was not suppressed.");
                    }

                    row.State = SegmentState.Active;
                    affected.Add(ToValue(row));
                    await DeriveAnalyzedStateAsync(db, rows, value.ItemId, row.Type, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case DeleteExternalSegmentIntent value:
                {
                    var mode = AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType)!.Value;
                    var match = rows.FirstOrDefault(row => row.Type == mode && row.StartTicks == externalTarget!.StartTicks && row.EndTicks == externalTarget.EndTicks && row.State == SegmentState.Active);
                    if (match is not null)
                    {
                        if (match.Source == SegmentSource.User)
                        {
                            db.Segments.Remove(match);
                        }
                        else
                        {
                            match.State = SegmentState.Suppressed;
                        }

                        affected.Add(ToValue(match));
                    }

                    externalOperations.Add(new ProjectedExternalOperation(value.ExternalSegmentId, value.ExpectedType, ProjectionExternalOperationKind.Delete));
                    await DeriveAnalyzedStateAsync(db, rows, value.ItemId, mode, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case WriteUserTimestampsIntent value:
                {
                    foreach (var timestamp in value.Timestamps)
                    {
                        var active = rows.Where(row => row.Type == timestamp.Mode && row.State == SegmentState.Active).ToList();
                        db.Segments.RemoveRange(active);
                        var tombstone = rows.FirstOrDefault(row => row.Type == timestamp.Mode && row.State == SegmentState.Suppressed && row.StartTicks == timestamp.StartTicks && row.EndTicks == timestamp.EndTicks);
                        var row = tombstone ?? new DbSegment(value.ItemId, timestamp.Mode, timestamp.StartTicks, timestamp.EndTicks, SegmentSource.User);
                        row.Source = SegmentSource.User;
                        row.State = SegmentState.Active;
                        if (tombstone is null)
                        {
                            db.Segments.Add(row);
                        }

                        affected.Add(ToValue(row));
                        await UpsertAnalyzedStateAsync(db, value.ItemId, timestamp.Mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

            case SegmentVisibilityChangeIntent value:
                {
                    var disabled = await db.DisabledItems.FirstOrDefaultAsync(item => item.ItemId == value.ItemId, cancellationToken).ConfigureAwait(false);
                    if (value.Visible && disabled is null)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.AlreadyVisible, "The item is already visible.");
                    }

                    if (!value.Visible && disabled is not null && disabled.SeasonId == value.SeasonId)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.AlreadyHidden, "The item is already hidden.");
                    }

                    if (value.Visible)
                    {
                        db.DisabledItems.Remove(disabled!);
                    }
                    else if (disabled is null)
                    {
                        db.DisabledItems.Add(new DbDisabledItem(value.SeasonId, value.ItemId));
                    }
                    else
                    {
                        disabled.SeasonId = value.SeasonId;
                    }

                    affected.AddRange(rows.Where(row => row.State == SegmentState.Active && (value.Visible || row.Source == SegmentSource.User)).Select(ToValue));
                    break;
                }

            default:
                return MutationResult.Reject(SegmentChangeRejectedReason.UnsupportedIntent, "Unsupported segment change intent.");
        }

        return new MutationResult(null, affected, externalOperations);
    }

    private static SegmentValue ToValue(DbSegment row) => new(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks, row.Source, row.State);

    private static async Task DeriveAnalyzedStateAsync(
        IntroSkipperDbContext db,
        IReadOnlyList<DbSegment> itemRows,
        Guid itemId,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var remaining = itemRows
            .Where(row => row.Type == mode && row.State == SegmentState.Active && db.Entry(row).State != EntityState.Deleted)
            .ToList();
        if (remaining.Any(row => row.Source == SegmentSource.User))
        {
            await UpsertAnalyzedStateAsync(db, itemId, mode, EpisodeState.UserProvided, string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (remaining.Count > 0)
        {
            var hashes = remaining.Select(row => row.ConfigHash).Distinct(StringComparer.Ordinal).ToList();
            await UpsertAnalyzedStateAsync(
                db,
                itemId,
                mode,
                EpisodeState.Analyzed,
                hashes.Count == 1 ? hashes[0] : string.Empty,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var existing = await db.AnalyzedStates.FirstOrDefaultAsync(
            value => value.ItemId == itemId && value.Type == mode,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            db.AnalyzedStates.Remove(existing);
        }
    }

    private static async Task UpsertAnalyzedStateAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        AnalysisMode mode,
        EpisodeState state,
        string configHash,
        CancellationToken cancellationToken)
    {
        var existing = await db.AnalyzedStates.FirstOrDefaultAsync(
            value => value.ItemId == itemId && value.Type == mode,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.AnalyzedStates.Add(new DbAnalyzedState(itemId, mode, state, configHash));
            return;
        }

        db.Entry(existing).Property(value => value.State).CurrentValue = state;
        db.Entry(existing).Property(value => value.ConfigHash).CurrentValue = configHash;
    }

    private static async Task<bool> HasAnalyzedStateAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        AnalysisMode mode,
        EpisodeState state,
        string configHash,
        CancellationToken cancellationToken)
        => await db.AnalyzedStates.AnyAsync(
            value => value.ItemId == itemId && value.Type == mode && value.State == state && value.ConfigHash == configHash,
            cancellationToken).ConfigureAwait(false);

    private static string Sanitize(Exception exception)
    {
        var value = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1024 ? value : value[..1024];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Projection failed for item {ItemId} sequence {Sequence}; it remains pending.")]
    private static partial void LogProjectionFailed(ILogger logger, Exception exception, Guid itemId, long sequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to reconcile segment projections after mirroring was enabled.")]
    private static partial void LogReconciliationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Segment projection recovery cycle failed; pending work will be retried.")]
    private static partial void LogRecoveryCycleFailed(ILogger logger, Exception exception);
}
