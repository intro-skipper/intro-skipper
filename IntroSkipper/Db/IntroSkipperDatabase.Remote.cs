// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.Remote;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.EntityFrameworkCore;
using SegmentScanner.Contracts;
using PluginMode = IntroSkipper.Data.AnalysisMode;
using RemoteMode = SegmentScanner.Contracts.RemoteAnalysisMode;

namespace IntroSkipper.Db;

/// <summary>
/// Remote analysis persistence and authoritative result admission.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    internal async Task<IReadOnlyList<RemoteNodeInfo>> GetRemoteNodesAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var nodes = await db.RemoteNodes.OrderBy(node => node.NodeId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return nodes.Select(ToNodeInfo).ToList();
    }

    internal async Task<IReadOnlyList<RemoteNodeInfo>> GetEnabledRemoteNodesAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var nodes = await db.RemoteNodes.Where(node => node.Enabled && node.NodeId != LocalNodeId).OrderBy(node => node.NodeId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return nodes.Select(ToNodeInfo).ToList();
    }

    internal async Task<RemoteNodeInfo?> GetRemoteNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var node = await db.RemoteNodes.FindAsync([nodeId], cancellationToken).ConfigureAwait(false);
        return node is null ? null : ToNodeInfo(node);
    }

    internal async Task<bool> IsCurrentRemoteNodeAsync(string nodeId, long revision, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.RemoteNodes.AnyAsync(node => node.NodeId == nodeId && node.Enabled && node.Revision == revision, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RemoteNodeInfo> RegisterRemoteNodeAsync(RemoteNodeRegistration registration, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRegistration(registration);
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var now = DateTime.UtcNow;
            var scopeJson = WireJson.Write(normalized);
            var node = await db.RemoteNodes.FindAsync([normalized.NodeId], cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                node = new DbRemoteNode
                {
                    NodeId = normalized.NodeId,
                    ScopeJson = scopeJson,
                    Enabled = false,
                    Revision = 1,
                    UpdatedUtc = now,
                    LastSeenUtc = now,
                };
                db.RemoteNodes.Add(node);
            }
            else if (!string.Equals(node.ScopeJson, scopeJson, StringComparison.Ordinal))
            {
                node.ScopeJson = scopeJson;
                node.Revision++;
                node.UpdatedUtc = now;
                node.LastSeenUtc = now;
            }
            else
            {
                node.LastSeenUtc = now;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToNodeInfo(node);
        }
    }

    internal async Task<RemoteNodeInfo?> SetRemoteNodeEnabledAsync(string nodeId, bool enabled, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var node = await db.RemoteNodes.FindAsync([nodeId], cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                return null;
            }

            if (node.Enabled != enabled)
            {
                node.Enabled = enabled;
                node.Revision++;
                node.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToNodeInfo(node);
        }
    }

    internal async Task<bool> QueueRemoteJobAsync(JobSnapshot snapshot, CancellationToken cancellationToken)
    {
        ContractValidation.Job(snapshot);
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (snapshot.NodeId is null || snapshot.NodeRevision is null)
            {
                return false;
            }

            var currentNode = await db.RemoteNodes.FindAsync([snapshot.NodeId], cancellationToken).ConfigureAwait(false);
            if (currentNode is null || !currentNode.Enabled || currentNode.Revision != snapshot.NodeRevision)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            var identityHash = RemoteJobIdentityHash(snapshot);
            var activeSnapshots = await db.RemoteJobs
                .Where(job => job.State == "Pending" || job.State == "Leased")
                .Select(job => job.SnapshotJson)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (activeSnapshots.Any(active => RemoteJobIdentityHash(WireJson.Read<JobSnapshot>(active)) == identityHash))
            {
                return true;
            }

            var json = WireJson.Write(snapshot);
            db.RemoteJobs.Add(new DbRemoteJob
            {
                Id = snapshot.JobId,
                AttemptId = snapshot.AttemptId,
                SnapshotJson = json,
                SnapshotHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(json)),
                State = "Pending",
                NodeId = snapshot.NodeId,
                NodeRevision = snapshot.NodeRevision,
                CreatedUtc = now,
                UpdatedUtc = now,
            });

            foreach (var item in snapshot.Items)
            {
                foreach (var mode in item.ExpectedModeRevisions.Keys)
                {
                    if (await db.RemoteAnalysisMetadata.FindAsync([item.ItemId, (int)mode], cancellationToken).ConfigureAwait(false) is null)
                    {
                        db.RemoteAnalysisMetadata.Add(new DbRemoteAnalysisMetadata { ItemId = item.ItemId, Mode = (int)mode });
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    internal async Task<JobSnapshot?> ClaimRemoteJobAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken, bool requireNodePolicy = false)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("A worker id is required.", nameof(workerId));
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var now = DateTime.UtcNow;
            DbRemoteJob? row;
            while (true)
            {
                row = await (
                    from job in db.RemoteJobs
                    join remoteNode in db.RemoteNodes on job.NodeId equals remoteNode.NodeId
                    where job.NodeId == workerId
                        && remoteNode.Enabled
                        && remoteNode.Revision == job.NodeRevision
                        && (job.State == "Pending" || (job.State == "Leased" && job.LeaseExpiresUtc < now))
                    orderby job.CreatedUtc
                    select job)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (row is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                try
                {
                    var parsedSnapshot = WireJson.Read<JobSnapshot>(row.SnapshotJson);
                    ContractValidation.Job(parsedSnapshot);
                    if (parsedSnapshot.NodeId != workerId || parsedSnapshot.NodeRevision != row.NodeRevision)
                    {
                        throw new InvalidDataException("Node-scoped job persistence does not match its snapshot.");
                    }

                    if (requireNodePolicy && (parsedSnapshot.NodePolicy is null || parsedSnapshot.ScanSettings is null))
                    {
                        row.State = "Superseded";
                        row.UpdatedUtc = now;
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
                {
                    row.State = "Rejected";
                    row.UpdatedUtc = now;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var current = row!;
            var token = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
            current.AttemptId = Guid.CreateVersion7();
            current.WorkerId = workerId;
            current.LeaseTokenHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(token));
            current.LeaseGeneration++;
            var node = await db.RemoteNodes.FindAsync([workerId], cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                throw new InvalidDataException("The current node disappeared during a claim.");
            }

            current.LeaseExpiresUtc = now.Add(TimeSpan.FromMinutes(ToNodeInfo(node).LeaseMinutes));
            node.LastSeenUtc = now;
            current.State = "Leased";
            current.Attempts++;
            current.UpdatedUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = WireJson.Read<JobSnapshot>(current.SnapshotJson) with
            {
                AttemptId = current.AttemptId,
                LeaseToken = token,
                LeaseGeneration = current.LeaseGeneration,
                LeaseExpiresUtc = new DateTimeOffset(current.LeaseExpiresUtc.Value),
            };
            return snapshot;
        }
    }

    internal async Task<bool> RenewRemoteLeaseAsync(Guid jobId, string token, int generation, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var tokenHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(token));
        var now = DateTime.UtcNow;
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var job = await db.RemoteJobs.SingleOrDefaultAsync(
                job => job.Id == jobId
                    && job.State == "Leased"
                    && job.LeaseGeneration == generation
                    && job.LeaseTokenHash == tokenHash
                    && job.LeaseExpiresUtc > now,
                cancellationToken).ConfigureAwait(false);
            if (job?.NodeId is null || job.NodeRevision is null)
            {
                return false;
            }

            var node = await db.RemoteNodes.FindAsync([job.NodeId], cancellationToken).ConfigureAwait(false);
            if (node is null || !node.Enabled || node.Revision != job.NodeRevision)
            {
                return false;
            }

            job.LeaseExpiresUtc = now.Add(TimeSpan.FromMinutes(ToNodeInfo(node).LeaseMinutes));
            node.LastSeenUtc = now;
            job.UpdatedUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    internal async Task<bool> ReleaseRemoteLeaseAsync(Guid jobId, string token, int generation, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var tokenHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(token));
        var now = DateTime.UtcNow;
        var updated = await db.RemoteJobs
            .Where(job => job.Id == jobId && job.State == "Leased" && job.LeaseGeneration == generation && job.LeaseTokenHash == tokenHash && job.LeaseExpiresUtc > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.State, "Pending")
                    .SetProperty(job => job.LeaseTokenHash, (string?)null)
                    .SetProperty(job => job.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(job => job.UpdatedUtc, now),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    internal async Task<IReadOnlyDictionary<RemoteMode, long>> GetRemoteRevisionsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var rows = await db.RemoteAnalysisMetadata.Where(row => row.ItemId == itemId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(row => (RemoteMode)row.Mode, row => row.Revision);
    }

    internal async Task<IReadOnlySet<Guid>> GetRemoteReviewHeldItemIdsAsync(JobSnapshot snapshot, CancellationToken cancellationToken)
    {
        var targets = snapshot.Items.Where(item => item.WriteTarget).ToDictionary(item => item.ItemId);
        if (targets.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        if (snapshot.NodeId is not null
            && (snapshot.NodeRevision is null
                || !await db.RemoteNodes.AnyAsync(
                    node => node.NodeId == snapshot.NodeId && node.Enabled && node.Revision == snapshot.NodeRevision,
                    cancellationToken).ConfigureAwait(false)))
        {
            return new HashSet<Guid>();
        }

        var targetIds = targets.Keys.ToArray();
        var held = await (
            from receipt in db.RemoteImportReceipts
            join job in db.RemoteJobs on receipt.JobId equals job.Id
            where receipt.Outcome == "Held" && targetIds.Contains(receipt.ItemId)
            select new { receipt.ItemId, receipt.Mode, job.SnapshotJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var heldIds = new HashSet<Guid>();
        foreach (var row in held)
        {
            var mode = (RemoteMode)row.Mode;
            if (!snapshot.Modes.Contains(mode))
            {
                continue;
            }

            var previous = WireJson.Read<JobSnapshot>(row.SnapshotJson);
            var previousItem = previous.Items.SingleOrDefault(item => item.ItemId == row.ItemId && item.WriteTarget);
            if (previousItem is not null
                && RemoteItemIdentityHash(previous, row.ItemId) == RemoteItemIdentityHash(snapshot, row.ItemId))
            {
                heldIds.Add(row.ItemId);
            }
        }

        return heldIds;
    }

    internal async Task<JobSnapshot?> ExportRemoteManifestAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var job = await db.RemoteJobs.SingleOrDefaultAsync(row => row.Id == jobId && row.State == "Pending", cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return null;
            }

            var contextId = Guid.CreateVersion7();
            var snapshot = WireJson.Read<JobSnapshot>(job.SnapshotJson) with
            {
                OfflineContextId = contextId.ToString("D"),
                LeaseToken = null,
                LeaseGeneration = 0,
                LeaseExpiresUtc = null,
            };
            var json = WireJson.Write(snapshot);
            db.RemoteExportContexts.Add(new DbRemoteExportContext
            {
                Id = contextId,
                SnapshotJson = json,
                SnapshotHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(json)),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(1),
            });
            job.State = "Exported";
            job.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
    }

    internal async Task<IReadOnlyList<ImportReceipt>> ImportRemoteResultsAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.OfflineContextId, out var contextId) || request.Results.Count is 0 or > 100)
        {
            throw new InvalidDataException("A bounded exported context and result bundle are required.");
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var context = await db.RemoteExportContexts.SingleOrDefaultAsync(row => row.Id == contextId, cancellationToken).ConfigureAwait(false);
        if (context is null || (context.ExpiresUtc is { } expires && expires <= DateTime.UtcNow))
        {
            throw new InvalidDataException("The exported context is unknown or expired.");
        }

        var snapshot = WireJson.Read<JobSnapshot>(context.SnapshotJson);
        if (snapshot.OfflineContextId != request.OfflineContextId)
        {
            throw new InvalidDataException("The exported context does not match its snapshot.");
        }

        return await SubmitRemoteResultsCoreAsync(snapshot.JobId, null, request.Results, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<CanonicalResult>> GetRemoteResultsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var rows = await db.RemoteAnalysisMetadata
            .Where(row => row.ItemId == itemId && row.CanonicalJson != null)
            .OrderBy(row => row.Mode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<CanonicalResult>(rows.Count);
        foreach (var row in rows)
        {
            var current = await CurrentCanonicalAsync(db, row.ItemId, (RemoteMode)row.Mode, null, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                results.Add(current);
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    internal async Task<IReadOnlyList<RevisionResponse>> GetRemoteRevisionResponsesAsync(IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var rows = await db.RemoteAnalysisMetadata
            .Where(row => itemIds.Contains(row.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return itemIds
            .SelectMany(itemId => Enum.GetValues<RemoteMode>()
                .Select(mode => new RevisionResponse(itemId, mode, rows.SingleOrDefault(row => row.ItemId == itemId && row.Mode == (int)mode)?.Revision ?? 0)))
            .ToArray();
    }

    internal static Task BumpRemoteRevisionsAsync(IntroSkipperDbContext db, Guid itemId, CancellationToken cancellationToken)
        => db.RemoteAnalysisMetadata
            .Where(row => row.ItemId == itemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Revision, row => row.Revision + 1)
                    .SetProperty(row => row.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);

    internal async Task<IReadOnlyList<ImportReceipt>> SubmitRemoteResultsAsync(Guid jobId, LeaseRequest lease, IReadOnlyList<AnalysisResult> results, CancellationToken cancellationToken)
        => await SubmitRemoteResultsCoreAsync(jobId, lease, results, cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<ImportReceipt>> ReviewRemoteResultsAsync(Guid jobId, RemoteReviewRequest request, CancellationToken cancellationToken)
        => await SubmitRemoteResultsCoreAsync(jobId, null, request.Results, cancellationToken, request.Review).ConfigureAwait(false);

    private async Task<IReadOnlyList<ImportReceipt>> SubmitRemoteResultsCoreAsync(Guid jobId, LeaseRequest? lease, IReadOnlyList<AnalysisResult> results, CancellationToken cancellationToken, ReviewDocument? review = null)
    {
        if (results.Count is 0 or > 100)
        {
            throw new InvalidDataException("A submission must contain 1 through 100 results.");
        }

        await InitializeAsync().ConfigureAwait(false);
        var reviewSelections = review is null ? null : ValidateReview(results, review);
        List<ImportReceipt> receipts = [];
        foreach (var rawResult in results.OrderBy(result => result.Mode == RemoteMode.Introduction ? 0 : 1))
        {
            ContractValidation.Result(rawResult);
            using var db = _contextFactory.CreateDbContext();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var job = await db.RemoteJobs.SingleOrDefaultAsync(row => row.Id == jobId, cancellationToken).ConfigureAwait(false);
                var selection = reviewSelections is null ? null : reviewSelections[rawResult.AnalysisId];
                var result = selection is null ? rawResult : ApplyReview(rawResult, selection);
                var payloadHash = selection is null ? WireJson.Hash(result) : HeldReviewHash(rawResult, selection);
                var rawPayloadHash = WireJson.Hash(rawResult);
                var existing = await db.RemoteImportReceipts.FindAsync([result.AnalysisId, result.ItemId, (int)result.Mode], cancellationToken).ConfigureAwait(false);
                if (existing is not null && selection is null)
                {
                    var replay = existing.PayloadHash == payloadHash;
                    var current = await CurrentCanonicalAsync(db, result.ItemId, result.Mode, null, cancellationToken).ConfigureAwait(false);
                    receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, replay ? existing.Outcome : "Conflict", replay ? existing.Reason : "PayloadMismatch", current));
                    continue;
                }

                if (selection is not null && (existing is null
                    || existing.JobId != jobId
                    || existing.Outcome is not ("Held" or "Accepted")
                    || (existing.Outcome == "Held" && existing.PayloadHash != rawPayloadHash)))
                {
                    var current = await CurrentCanonicalAsync(db, result.ItemId, result.Mode, null, cancellationToken).ConfigureAwait(false);
                    receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, "Rejected", "HeldResultMismatch", current));
                    continue;
                }

                if (selection is not null && existing!.Outcome == "Accepted")
                {
                    var replay = existing.PayloadHash == payloadHash;
                    var current = await CurrentCanonicalAsync(db, result.ItemId, result.Mode, null, cancellationToken).ConfigureAwait(false);
                    receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, replay ? "Accepted" : "Conflict", replay ? existing.Reason : "PayloadMismatch", current));
                    continue;
                }

                if (job is null || (selection is null && (lease is null ? job.State != "Exported" : !LeaseMatches(job, lease, DateTime.UtcNow))))
                {
                    receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, "Rejected", "LeaseExpired", null));
                    continue;
                }

                ContractValidation.Result(result);
                var snapshot = WireJson.Read<JobSnapshot>(job.SnapshotJson);
                if (selection is null && snapshot.NodeId is null)
                {
                    receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, "Rejected", "NodeRegistrationRequired", null));
                    continue;
                }

                var item = snapshot.Items.SingleOrDefault(item => item.ItemId == result.ItemId && item.WriteTarget);
                var serverRuntimeTicks = 0L;
                var reason = ValidateResultAgainstSnapshot(result, job, snapshot, item);
                if (reason is null)
                {
                    reason = await ValidateCurrentPolicyAsync(db, result, snapshot, item, cancellationToken).ConfigureAwait(false);
                }

                if (reason is null)
                {
                    reason = ValidateCurrentInput(result, item, out serverRuntimeTicks);
                }

                if (reason is null)
                {
                    reason = await ValidateCurrentRevisionAsync(db, result, cancellationToken).ConfigureAwait(false);
                }

                var automaticImport = false;
                if (reason is null && selection is null && snapshot.NodeId is not null)
                {
                    var node = await db.RemoteNodes.FindAsync([snapshot.NodeId], cancellationToken).ConfigureAwait(false);
                    if (node is { Enabled: true } && node.Revision == snapshot.NodeRevision && ToNodeInfo(node).AutomaticImports)
                    {
                        var approved = ApplyAutomaticImportPolicy(result, snapshot);
                        if (approved is not null)
                        {
                            result = approved;
                            automaticImport = true;
                        }
                    }
                }

                if (reason is null && result.Status == AnalysisStatus.CompletedNoSegments)
                {
                    reason = ValidateNegativeSearchCoverage(result, snapshot);
                }

                if (reason is null
                    && result.Status is AnalysisStatus.CompletedWithSegments or AnalysisStatus.CompletedNoSegments
                    && result.Mode == RemoteMode.Credits
                    && selection is null
                    && !automaticImport
                    && !(Plugin.Instance?.Configuration.ApprovedRemoteCreditsProfileHashes.Contains(result.Detector.ProfileHash) ?? false))
                {
                    reason = "CreditsProfileNotApproved";
                }

                if (reason is null
                    && result.Status is AnalysisStatus.CompletedWithSegments or AnalysisStatus.CompletedNoSegments
                    && selection is null
                    && !automaticImport
                    && !result.Detector.ProductionApproved)
                {
                    reason = "DetectorNotApproved";
                }

                CanonicalResult? canonical = null;
                var outcome = reason is null ? "Accepted" : "Rejected";
                if (reason is null && result.Status is AnalysisStatus.CompletedWithSegments or AnalysisStatus.CompletedNoSegments)
                {
                    var mode = ToPluginMode(result.Mode);
                    var accepted = result.Segments
                        .Where(segment => segment.Decision == CandidateDecision.Accept)
                        .Select(segment => new AttributedSegment(
                            new Segment(
                                result.ItemId,
                                new IntroSkipper.Data.TimeRange(
                                    TickConversions.ToSeconds(segment.StartTicks),
                                    TickConversions.ToSeconds(segment.EndTicks))),
                            SegmentSource.Remote))
                        .ToArray();
                    await ReplaceAutoSegmentsInContextAsync(db, result.ItemId, mode, accepted, derivedWrite: false, item!.ServerAnalysisConfigHash, cancellationToken).ConfigureAwait(false);
                    var analysis = await db.AnalyzedItems.FindAsync([result.ItemId, mode], cancellationToken).ConfigureAwait(false);
                    if (analysis is null)
                    {
                        analysis = new DbAnalyzedItem(result.ItemId, mode, item.ServerAnalysisConfigHash);
                        db.AnalyzedItems.Add(analysis);
                    }

                    analysis.ConfigHash = item.ServerAnalysisConfigHash;
                    analysis.FileVersion = long.Parse(result.FileVersionToken, System.Globalization.CultureInfo.InvariantCulture);
                    var canonicalResult = result.TimelineEndTicks > 0 && result.TimelineEndTicks < serverRuntimeTicks
                        && !result.Warnings.Contains("VisualTimelineShorterThanServerRuntime", StringComparer.Ordinal)
                        ? result with { Warnings = [.. result.Warnings, "VisualTimelineShorterThanServerRuntime"] }
                        : result;
                    canonical = await UpsertCanonicalAsync(db, canonicalResult, cancellationToken).ConfigureAwait(false);
                }
                else if (reason is null)
                {
                    outcome = result.Status == AnalysisStatus.NeedsReview ? "Held" : "Retained";
                    canonical = await CurrentCanonicalAsync(db, result.ItemId, result.Mode, null, cancellationToken).ConfigureAwait(false);
                }

                if (selection is not null && reason is not null)
                {
                    var current = await CurrentCanonicalAsync(db, result.ItemId, result.Mode, null, cancellationToken).ConfigureAwait(false);
                    receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, "Rejected", reason, current));
                    continue;
                }

                if (selection is null)
                {
                    db.RemoteImportReceipts.Add(new DbRemoteImportReceipt
                    {
                        JobId = job.Id,
                        AnalysisId = result.AnalysisId,
                        ItemId = result.ItemId,
                        Mode = (int)result.Mode,
                        PayloadHash = payloadHash,
                        Outcome = outcome,
                        Reason = reason,
                        OriginalCanonicalJson = canonical is null ? null : WireJson.Write(canonical),
                        CreatedUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing!.PayloadHash = payloadHash;
                    existing.Outcome = outcome;
                    existing.Reason = reason;
                    existing.OriginalCanonicalJson = canonical is null ? null : WireJson.Write(canonical);
                    existing.CreatedUtc = DateTime.UtcNow;
                }

                var expectedPairs = snapshot.Items
                    .Where(item => item.WriteTarget)
                    .SelectMany(item => snapshot.Modes.Select(mode => (item.ItemId, Mode: (int)mode)))
                    .ToHashSet();
                var completedPairs = (await db.RemoteImportReceipts
                    .Where(receipt => receipt.JobId == job.Id)
                    .Select(receipt => new { receipt.ItemId, receipt.Mode })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                    .Select(receipt => (receipt.ItemId, receipt.Mode))
                    .Where(expectedPairs.Contains)
                    .ToHashSet();
                var currentPair = (result.ItemId, Mode: (int)result.Mode);
                if (expectedPairs.Contains(currentPair))
                {
                    completedPairs.Add(currentPair);
                }

                if (completedPairs.Count == expectedPairs.Count)
                {
                    job.State = "Completed";
                    job.UpdatedUtc = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                receipts.Add(new ImportReceipt(result.AnalysisId, result.ItemId, result.Mode, outcome, reason, canonical));
            }
        }

        return receipts;
    }

    private static AnalysisResult? ApplyAutomaticImportPolicy(AnalysisResult result, JobSnapshot snapshot)
    {
        if (result.Mode is RemoteMode.Recap or RemoteMode.Preview or RemoteMode.Commercial)
        {
            if (snapshot.ScanSettings is null || result.Detector.Id != "introskipper-plugin-v3"
                || result.Detector.ProfileHash != WireJson.Hash(snapshot.ScanSettings) || result.Detector.ModelSha256 is not null
                || result.Status is not (AnalysisStatus.NeedsReview or AnalysisStatus.CompletedNoSegments)
                || result.Warnings.Count != 1 || result.Warnings[0] != "SharedPluginSettings"
                || result.Segments.Any(segment => segment.Decision != CandidateDecision.Review
                    || segment.Reasons.Count != 2 || !segment.Reasons.Contains("SharedPluginSettings", StringComparer.Ordinal)
                    || !segment.Reasons.Contains("UpstreamAdjusted", StringComparer.Ordinal)))
            {
                return null;
            }

            return result with
            {
                Status = result.Segments.Count == 0 ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments,
                Segments = result.Segments.Select(segment => segment with { Decision = CandidateDecision.Accept, ConfidenceTier = "ProfileOptIn" }).ToList(),
                Warnings = [.. result.Warnings, "AutomaticImportByNodePolicy"],
            };
        }

        if (snapshot.CreditsMethod == CreditsDetectionMethod.IntroSkipper)
        {
            if (result.Mode != RemoteMode.Credits || result.Status != AnalysisStatus.NeedsReview
                || result.Detector.Id != (snapshot.CreditsRecipe?.UseGpu == true ? "introskipper-credits-gpu-v1" : "introskipper-credits-cpu-v1") || result.Detector.ModelSha256 is not null
                || string.IsNullOrWhiteSpace(result.Detector.InferenceEnvironment)
                || result.Warnings.Count != 1 || result.Warnings[0] != "OriginalIntroSkipperCredits"
                || result.Segments.Any(segment => segment.Decision != CandidateDecision.Review
                    || segment.Reasons.Count != 2 || !segment.Reasons.Contains("OriginalIntroSkipperCredits", StringComparer.Ordinal)
                    || !segment.Reasons.Contains("UpstreamAdjusted", StringComparer.Ordinal)
                    || segment.RawStartTicks is not long rawStart || segment.RawEndTicks is not long rawEnd || rawStart >= rawEnd
                    || !CoversRange(result.SearchedRanges, Math.Min(rawStart, segment.StartTicks), Math.Max(rawEnd, segment.EndTicks))))
            {
                return null;
            }

            var (start, end) = OriginalCreditsSearchWindow(result, snapshot);
            if (!CoversRange(result.SearchedRanges, start, end))
            {
                return null;
            }

            return result with
            {
                Status = result.Segments.Count == 0 ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments,
                Segments = result.Segments.Select(segment => segment with { Decision = CandidateDecision.Accept, ConfidenceTier = "ProfileOptIn" }).ToList(),
                Warnings = [.. result.Warnings, "AutomaticImportByNodePolicy"],
            };
        }

        // The node opts into this exact profile; calibration and raw receipt hashes stay unchanged.
        if (result.Mode != RemoteMode.Credits || result.Status != AnalysisStatus.NeedsReview
            || result.Detector.Id != "plex-observed-experimental"
            || !ContractValidation.IsSha256(result.Detector.ModelSha256)
            || string.IsNullOrWhiteSpace(result.Detector.InferenceEnvironment)
            || result.SearchedRanges.Count != 1 || result.SearchedRanges[0].EndTicks != result.TimelineEndTicks
            || !result.Warnings.Contains("ExperimentalCreditsGroupPolicy", StringComparer.Ordinal)
            || result.Warnings.Any(warning => warning is not ("ExperimentalCreditsGroupPolicy" or "StaticTextGroupRejected"
                or "SparseSceneTextGroupRejected" or "FiveSampleGroupRejected" or "TenSampleGroupRejected"
                or "TextPositionRejected" or "IsolatedEarlyGroupRejected" or "NoTextSpanBoundary"))
            || result.Segments.Any(segment => segment.Decision != CandidateDecision.Review
                || !segment.Reasons.Contains("OrderedCreditsGroupPolicy", StringComparer.Ordinal)
                || !segment.Reasons.Contains("ProfileNotCalibrated", StringComparer.Ordinal)
                || segment.Reasons.Any(reason => reason is not ("OrderedCreditsGroupPolicy" or "ProfileNotCalibrated"
                    or "SampleIndexGapJoined" or "ShortGapJoined" or "FlatGapJoined" or "TextGapJoined"
                    or "FinalSamplesReachEOF" or "FlatTailAbsorbed"))
                || segment.RawStartTicks is not long rawStart || segment.RawEndTicks is not long rawEnd || rawStart >= rawEnd
                || !CoversRange(result.SearchedRanges, Math.Min(rawStart, segment.StartTicks), Math.Max(rawEnd, segment.EndTicks))))
        {
            return null;
        }

        if (result.Segments.Count == 0)
        {
            var (start, end) = CreditsSearchWindow(result);
            if (!CoversRange(result.SearchedRanges, start, end))
            {
                return null;
            }
        }

        return result with
        {
            Status = result.Segments.Count == 0 ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments,
            Segments = result.Segments.Select(segment => segment with { Decision = CandidateDecision.Accept, ConfidenceTier = "ProfileOptIn" }).ToList(),
            Warnings = [.. result.Warnings, "AutomaticImportByNodePolicy"],
        };
    }

    private static Dictionary<Guid, ReviewSelection> ValidateReview(IReadOnlyList<AnalysisResult> results, ReviewDocument review)
    {
        var decisions = review.Decisions ?? throw new InvalidDataException("Unsupported review document.");
        var acceptNoSegments = review.AcceptNoSegments ?? throw new InvalidDataException("Unsupported review document.");
        ContractValidation.Require(
            review.SchemaVersion == 1,
            "Unsupported review document.");
        ContractValidation.Require(
            results.Select(result => result.AnalysisId).Distinct().Count() == results.Count,
            "Review results must have unique analysis identities.");
        ContractValidation.Require(
            acceptNoSegments.Distinct().Count() == acceptNoSegments.Count,
            "Duplicate zero-candidate review acceptance.");

        var byAnalysis = results.ToDictionary(result => result.AnalysisId);
        ContractValidation.Require(
            acceptNoSegments.All(byAnalysis.ContainsKey),
            "Review accepts an unknown analysis.");
        ContractValidation.Require(
            decisions.All(decision => byAnalysis.ContainsKey(decision.AnalysisId)
                && !string.IsNullOrWhiteSpace(decision.CandidateId)
                && decision.Decision is CandidateDecision.Accept or CandidateDecision.Reject),
            "Review contains an unknown or unresolved candidate decision.");
        ContractValidation.Require(
            decisions.GroupBy(decision => (decision.AnalysisId, decision.CandidateId)).All(group => group.Count() == 1),
            "Review contains duplicate candidate decisions.");

        var selections = new Dictionary<Guid, ReviewSelection>();
        foreach (var result in results)
        {
            var resultDecisions = decisions
                .Where(decision => decision.AnalysisId == result.AnalysisId)
                .OrderBy(decision => decision.CandidateId, StringComparer.Ordinal)
                .ToList();
            var candidates = result.Segments
                .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .ToList();
            var acceptsNoSegments = acceptNoSegments.Contains(result.AnalysisId);
            if (candidates.Count == 0)
            {
                ContractValidation.Require(
                    acceptsNoSegments && resultDecisions.Count == 0,
                    "Zero-candidate reviews require explicit acceptance.");
            }
            else
            {
                ContractValidation.Require(
                    !acceptsNoSegments
                    && resultDecisions.Count == candidates.Count
                    && resultDecisions.Select(decision => decision.CandidateId).SequenceEqual(candidates.Select(candidate => candidate.CandidateId), StringComparer.Ordinal),
                    "Review must decide every candidate exactly once.");
            }

            selections.Add(result.AnalysisId, new ReviewSelection(resultDecisions, acceptsNoSegments));
        }

        return selections;
    }

    private static AnalysisResult ApplyReview(AnalysisResult result, ReviewSelection selection)
    {
        var decisions = selection.Decisions.ToDictionary(decision => decision.CandidateId, StringComparer.Ordinal);
        var accepted = result.Segments
            .Select(candidate =>
            {
                var decision = decisions[candidate.CandidateId];
                return candidate with
                {
                    Decision = decision.Decision,
                    StartTicks = decision.StartTicks ?? candidate.StartTicks,
                    EndTicks = decision.EndTicks ?? candidate.EndTicks,
                };
            })
            .Where(candidate => candidate.Decision == CandidateDecision.Accept)
            .ToList();
        return result with
        {
            Status = accepted.Count == 0 ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments,
            Segments = accepted,
        };
    }

    private static string HeldReviewHash(AnalysisResult result, ReviewSelection selection)
        => WireJson.Hash(new
        {
            Domain = "held-review-v1",
            Result = result,
            Decisions = selection.Decisions,
            selection.AcceptNoSegments,
        });

    private static bool LeaseMatches(DbRemoteJob job, LeaseRequest lease, DateTime now)
        => job.State == "Leased" && job.LeaseGeneration == lease.LeaseGeneration && job.LeaseExpiresUtc > now && job.LeaseTokenHash == WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(lease.LeaseToken));

    private static string RemoteJobIdentityHash(JobSnapshot snapshot)
        => WireJson.Hash(snapshot with
        {
            JobId = Guid.Empty,
            AttemptId = Guid.Empty,
            LeaseToken = null,
            LeaseGeneration = 0,
            LeaseExpiresUtc = null,
            OfflineContextId = null,
        });

    private static RemoteNodeRegistration NormalizeRegistration(RemoteNodeRegistration registration)
    {
        NodeScope.ValidateRegistration(registration);
        return registration with
        {
            LibraryIds = registration.LibraryIds.Order().ToList(),
            Modes = registration.Modes.Order().ToList(),
            ServerPaths = registration.ServerPaths
                .Select(path => path with { Prefix = NodeScope.NormalizeAbsolutePath(path.Prefix) })
                .OrderBy(path => path.Prefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path.CaseSensitive)
                .ToList(),
        };
    }

    private static RemoteNodeRegistration ToRegistration(DbRemoteNode node)
        => WireJson.Read<RemoteNodeRegistration>(node.ScopeJson);

    private static RemoteNodeInfo ToNodeInfo(DbRemoteNode node)
    {
        var registration = ToRegistration(node);
        var policy = node.PolicyJson is null ? null : WireJson.Read<RemoteNodePolicy>(node.PolicyJson);
        var runtime = node.RuntimeJson is null ? null : WireJson.Read<NodeRuntimeInformation>(node.RuntimeJson);
        var recipe = runtime?.CreditsRecipes.FirstOrDefault(recipe => recipe.Method == policy?.CreditsMethod && recipe.UseGpu == policy.UseGpu);
        return new RemoteNodeInfo
        {
            NodeId = registration.NodeId,
            Name = registration.Name,
            LibraryIds = policy?.LibraryIds ?? registration.LibraryIds,
            Modes = policy?.Modes ?? registration.Modes,
            ServerPaths = policy?.PathMappings.Select(path => new RemoteNodePath { Prefix = path.ServerPrefix, CaseSensitive = path.CaseSensitive }).ToList() ?? registration.ServerPaths,
            CreditsProfileHash = recipe?.ProfileHash ?? registration.CreditsProfileHash,
            CreditsMethod = policy?.CreditsMethod ?? registration.CreditsMethod,
            AutomaticImports = policy?.AutomaticImports ?? registration.AutomaticImports,
            LeaseMinutes = policy?.LeaseMinutes ?? registration.LeaseMinutes,
            Policy = policy,
            Runtime = runtime,
            IsLocal = node.NodeId == LocalNodeId,
            LastSeenUtc = new DateTimeOffset(DateTime.SpecifyKind(node.LastSeenUtc, DateTimeKind.Utc)),
            Enabled = node.Enabled,
            Revision = node.Revision,
            UpdatedUtc = new DateTimeOffset(DateTime.SpecifyKind(node.UpdatedUtc, DateTimeKind.Utc)),
        };
    }

    private static string RemoteItemIdentityHash(JobSnapshot snapshot, Guid itemId)
        => RemoteJobIdentityHash(snapshot with
        {
            Items = snapshot.Items
                .Select(item => item with { WriteTarget = item.ItemId == itemId })
                .ToList(),
        });

    private static string? ValidateResultAgainstSnapshot(AnalysisResult result, DbRemoteJob job, JobSnapshot snapshot, JobItem? item)
    {
        if (item is null
            || result.JobId != snapshot.JobId
            || result.ServerId != snapshot.ServerId
            || result.AttemptId != job.AttemptId
            || !snapshot.Modes.Contains(result.Mode))
        {
            return "SnapshotMismatch";
        }

        if (result.MediaSourceKey != item.MediaSourceKey
            || result.SourcePath != item.SourcePath
            || result.FileVersionToken != item.FileVersionToken
            || string.IsNullOrWhiteSpace(item.ServerAnalysisConfigHash)
            || !item.ExpectedModeRevisions.TryGetValue(result.Mode, out var revision)
            || result.ExpectedItemModeRevision != revision)
        {
            return "InputChanged";
        }

        if (result.Mode == RemoteMode.Introduction && result.Detector.ProfileHash != snapshot.IntroductionProfileHash)
        {
            return "IntroductionProfileChanged";
        }

        if (snapshot.ServerId is not null && result.Mode == RemoteMode.Credits && result.Detector.ProfileHash != snapshot.CreditsProfileHash)
        {
            return "CreditsProfileChanged";
        }

        if (result.Mode is RemoteMode.Recap or RemoteMode.Preview or RemoteMode.Commercial
            && (snapshot.ScanSettings is null || result.Detector.ProfileHash != WireJson.Hash(snapshot.ScanSettings)))
        {
            return "SharedProfileChanged";
        }

        if (result.Status is AnalysisStatus.CompletedWithSegments or AnalysisStatus.CompletedNoSegments
            && RemoteAnalysisDispatcher.HasUnknownChapterDependency(result, item, snapshot.IntroductionSettings))
        {
            return "ChapterProvenanceUnknown";
        }

        return null;
    }

    private static string? ValidateCurrentInput(AnalysisResult result, JobItem? item, out long serverRuntimeTicks)
    {
        serverRuntimeTicks = 0;
        var current = MediaItemHelper.FindSupported(result.ItemId);
        if (current is null || item is null || current.Path != item.SourcePath)
        {
            return "InputChanged";
        }

        var currentVersion = (SeasonResolver.FileVersion(current) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (currentVersion != result.FileVersionToken)
        {
            return "InputChanged";
        }

        if (RemoteAnalysisDispatcher.GetChapterInputHash(item.Chapters) != RemoteAnalysisDispatcher.GetChapterInputHash(RemoteAnalysisDispatcher.GetChapterSnapshots(result.ItemId)))
        {
            return "ChapterInputChanged";
        }

        serverRuntimeTicks = current.RunTimeTicks ?? item.RuntimeTicks;
        return serverRuntimeTicks > 0 && result.TimelineEndTicks > serverRuntimeTicks ? "TimelineExceedsServerRuntime" : null;
    }

    private static async Task<string?> ValidateCurrentPolicyAsync(IntroSkipperDbContext db, AnalysisResult result, JobSnapshot snapshot, JobItem? item, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var configuration = plugin?.Configuration;
        var mode = ToPluginMode(result.Mode);
        if (configuration is null || item is null)
        {
            return "RemotePolicyChanged";
        }

        RemoteNodeInfo? nodeRegistration = null;
        if (snapshot.NodeId is null)
        {
            if (!configuration.RemoteAnalysisEnabled || !configuration.RemoteAnalysisModes.Contains(mode))
            {
                return "RemotePolicyChanged";
            }
        }
        else
        {
            var node = await db.RemoteNodes.FindAsync([snapshot.NodeId], cancellationToken).ConfigureAwait(false);
            if (node is null || !node.Enabled || node.Revision != snapshot.NodeRevision)
            {
                return "RemotePolicyChanged";
            }

            nodeRegistration = ToNodeInfo(node);
            var currentItem = MediaItemHelper.FindSupported(result.ItemId);
            if (!nodeRegistration.Modes.Contains(result.Mode)
                || currentItem is null
                || !plugin!.IsInLibrary(currentItem, nodeRegistration.LibraryIds)
                || !nodeRegistration.ServerPaths.Any(path => path.Matches(currentItem.Path)))
            {
                return "RemotePolicyChanged";
            }

            if (result.Mode == RemoteMode.Credits && (result.Detector.ProfileHash != nodeRegistration.CreditsProfileHash || snapshot.CreditsProfileHash != nodeRegistration.CreditsProfileHash))
            {
                return "CreditsProfileChanged";
            }

            if (result.Mode == RemoteMode.Credits && snapshot.CreditsMethod != nodeRegistration.CreditsMethod)
            {
                return "CreditsMethodChanged";
            }
        }

        if (!Guid.TryParse(item.SeasonKey, out var seasonId))
        {
            return "RemotePolicyChanged";
        }

        var season = plugin?.GetItem(seasonId);
        var current = MediaItemHelper.FindSupported(result.ItemId);
        var series = season is null ? current : plugin?.GetItem(season.ParentId) ?? current;
        if (series is null || (nodeRegistration is null && !plugin!.IsInLibrary(series, configuration.RemoteAnalysisLibraryIds)))
        {
            return "RemotePolicyChanged";
        }

        var overrides = await db.SeasonAnalysisOverrides
            .Where(row => row.SeasonId == seasonId)
            .Select(row => new
            {
                row.AnalysisPercent,
                row.AnalysisLengthLimit,
                row.PreviewFromCreditsEnd,
                row.SubtitleRecapDetection,
                row.SubtitlePreviewDetection,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var action = await db.SeasonStates
            .Where(row => row.SeasonId == seasonId && row.Type == mode)
            .Select(row => (AnalyzerAction?)row.Action)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? AnalyzerAction.Default;
        var previewFromCreditsEnd = overrides?.PreviewFromCreditsEnd
            ?? (series is Series televisionSeries && SeriesHelper.IsAnime(televisionSeries) && configuration.AnimePreviewFromCreditsEnd);
        var currentHashes = new[]
        {
            ConfigHasher.Analysis(
                configuration,
                mode,
                action,
                ffmpegValid: false,
                analysisPercentOverride: overrides?.AnalysisPercent,
                analysisLengthLimitOverride: overrides?.AnalysisLengthLimit,
                previewFromCreditsEndOverride: previewFromCreditsEnd,
                subtitleRecapDetectionOverride: overrides?.SubtitleRecapDetection,
                subtitlePreviewDetectionOverride: overrides?.SubtitlePreviewDetection),
            ConfigHasher.Analysis(
                configuration,
                mode,
                action,
                ffmpegValid: true,
                analysisPercentOverride: overrides?.AnalysisPercent,
                analysisLengthLimitOverride: overrides?.AnalysisLengthLimit,
                previewFromCreditsEndOverride: previewFromCreditsEnd,
                subtitleRecapDetectionOverride: overrides?.SubtitleRecapDetection,
                subtitlePreviewDetectionOverride: overrides?.SubtitlePreviewDetection),
        };
        if (!currentHashes.Contains(item.ServerAnalysisConfigHash, StringComparer.Ordinal))
        {
            return "AnalysisConfigChanged";
        }

        if (result.Mode == RemoteMode.Introduction)
        {
            var profileHash = RemoteAnalysisDispatcher.GetIntroductionProfileHash(
                configuration,
                overrides?.AnalysisPercent,
                overrides?.AnalysisLengthLimit,
                action);
            if (result.Detector.ProfileHash != profileHash)
            {
                return "IntroductionProfileChanged";
            }
        }

        if (result.Mode == RemoteMode.Credits && snapshot.CreditsMethod == CreditsDetectionMethod.IntroSkipper
            && (snapshot.CreditsSettings is not { } creditsSettings
                || WireJson.Hash(creditsSettings) != WireJson.Hash(RemoteAnalysisDispatcher.GetCreditsSettings(configuration, action))))
        {
            return "CreditsSettingsChanged";
        }

        if (snapshot.ScanSettings is { } scanSettings)
        {
            var effective = new QueuedEpisode
            {
                AnalysisPercentOverride = overrides?.AnalysisPercent,
                AnalysisLengthLimitOverride = overrides?.AnalysisLengthLimit,
                PreviewFromCreditsEndOverride = previewFromCreditsEnd,
                SubtitleRecapDetectionOverride = overrides?.SubtitleRecapDetection,
                SubtitlePreviewDetectionOverride = overrides?.SubtitlePreviewDetection,
            };
            if (WireJson.Hash(scanSettings) != WireJson.Hash(RemoteAnalysisDispatcher.GetScanSettings(configuration, effective, action)))
            {
                return "SharedSettingsChanged";
            }
        }

        if (item.ContextRevisions is { } contextRevisions)
        {
            foreach (var context in contextRevisions)
            {
                var contextMode = ToPluginMode(context.Key);
                var segments = await db.Segments.Where(segment => segment.ItemId == item.ItemId && segment.Type == contextMode && segment.State == SegmentState.Active)
                    .OrderBy(segment => segment.StartTicks).ThenBy(segment => segment.EndTicks)
                    .Select(segment => new ContextSegment(context.Key, segment.StartTicks, segment.EndTicks)).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (WireJson.Hash(segments) != context.Value)
                {
                    return "ContextSegmentsChanged";
                }
            }
        }

        if (snapshot.NodeId is null && snapshot.ServerId is not null && result.Mode == RemoteMode.Credits
            && (!ContractValidation.IsSha256(configuration.RemoteCreditsProfileHash)
                || result.Detector.ProfileHash != configuration.RemoteCreditsProfileHash))
        {
            return "CreditsProfileChanged";
        }

        return null;
    }

    private static async Task<string?> ValidateCurrentRevisionAsync(IntroSkipperDbContext db, AnalysisResult result, CancellationToken cancellationToken)
    {
        var currentRevision = await db.RemoteAnalysisMetadata
            .Where(row => row.ItemId == result.ItemId && row.Mode == (int)result.Mode)
            .Select(row => (long?)row.Revision)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
        return result.ExpectedItemModeRevision == currentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ? null : "RevisionChanged";
    }

    private static string? ValidateNegativeSearchCoverage(AnalysisResult result, JobSnapshot snapshot)
    {
        if (result.SearchedRanges.Count == 0)
        {
            return "IncompleteNegativeSearchCoverage";
        }

        var (start, end) = result.Mode switch
        {
            RemoteMode.Introduction => IntroductionSearchWindow(result, snapshot),
            RemoteMode.Credits when snapshot.CreditsMethod == CreditsDetectionMethod.IntroSkipper => OriginalCreditsSearchWindow(result, snapshot),
            RemoteMode.Credits => CreditsSearchWindow(result),
            RemoteMode.Recap => IntroductionSearchWindow(result, snapshot),
            RemoteMode.Preview when snapshot.ScanSettings is { } settings
                && settings.Configuration.TryGetProperty("maximumPreviewDuration", out var duration) && duration.TryGetInt32(out var seconds)
                => (Math.Max(0, result.TimelineEndTicks - TimeSpan.FromSeconds(seconds).Ticks), result.TimelineEndTicks),
            RemoteMode.Commercial => (0L, result.TimelineEndTicks),
            _ => (0L, 0L),
        };
        return CoversRange(result.SearchedRanges, start, end) ? null : "IncompleteNegativeSearchCoverage";
    }

    private static (long Start, long End) IntroductionSearchWindow(AnalysisResult result, JobSnapshot snapshot)
    {
        if (!snapshot.IntroductionSettings.TryGetProperty("analysisPercent", out var analysisPercent)
            || !snapshot.IntroductionSettings.TryGetProperty("analysisLengthLimit", out var analysisLengthLimit)
            || !analysisPercent.TryGetInt32(out var percent)
            || !analysisLengthLimit.TryGetInt32(out var lengthLimit)
            || percent is < 1 or > 100
            || lengthLimit < 1)
        {
            return (0, long.MaxValue);
        }

        var visualSeconds = TimeSpan.FromTicks(result.TimelineEndTicks).TotalSeconds;
        var searchedSeconds = Math.Min(
            visualSeconds >= TimeSpan.FromMinutes(5).TotalSeconds ? visualSeconds * percent / 100 : visualSeconds,
            TimeSpan.FromMinutes(lengthLimit).TotalSeconds);
        return (0, TimeSpan.FromSeconds(searchedSeconds).Ticks);
    }

    private static (long Start, long End) CreditsSearchWindow(AnalysisResult result)
        => (result.TimelineEndTicks - (result.TimelineEndTicks / 5), result.TimelineEndTicks);

    private static (long Start, long End) OriginalCreditsSearchWindow(AnalysisResult result, JobSnapshot snapshot)
    {
        var item = snapshot.Items.SingleOrDefault(item => item.ItemId == result.ItemId);
        var property = item?.IsMovie == true ? "maximumMovieCreditsDuration" : "maximumCreditsDuration";
        if (snapshot.CreditsSettings is not { } settings
            || !settings.TryGetProperty("configuration", out var configuration)
            || !configuration.TryGetProperty(property, out var duration) || !duration.TryGetInt32(out var seconds) || seconds <= 0)
        {
            return (0, long.MaxValue);
        }

        return (Math.Max(0, result.TimelineEndTicks - TimeSpan.FromSeconds(seconds).Ticks), result.TimelineEndTicks);
    }

    private static bool CoversRange(IReadOnlyList<SegmentScanner.Contracts.TimeRange> ranges, long start, long end)
    {
        if (start < 0 || end <= start)
        {
            return false;
        }

        var covered = start;
        foreach (var range in ranges.OrderBy(range => range.StartTicks))
        {
            if (range.EndTicks <= covered)
            {
                continue;
            }

            if (range.StartTicks > covered)
            {
                return false;
            }

            covered = range.EndTicks;
            if (covered >= end)
            {
                return true;
            }
        }

        return false;
    }

    private static PluginMode ToPluginMode(RemoteMode mode) => RemoteModes.ToPlugin(mode);

    private static async Task<CanonicalResult?> CurrentCanonicalAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        RemoteMode mode,
        AnalysisResult? fallback,
        CancellationToken cancellationToken)
    {
        var row = await db.RemoteAnalysisMetadata.FindAsync([itemId, (int)mode], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var prior = row.CanonicalJson is null ? null : WireJson.Read<CanonicalResult>(row.CanonicalJson);
        var result = fallback ?? prior?.Result;
        if (result is null)
        {
            return null;
        }

        var persisted = await db.Segments
            .Where(segment => segment.ItemId == itemId && segment.Type == ToPluginMode(mode) && segment.State == SegmentState.Active)
            .OrderBy(segment => segment.StartTicks)
            .ThenBy(segment => segment.EndTicks)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = persisted.ToDictionary(segment => segment.Id);
        foreach (var entry in db.ChangeTracker.Entries<DbSegment>())
        {
            var segment = entry.Entity;
            if (segment.ItemId != itemId || segment.Type != ToPluginMode(mode))
            {
                continue;
            }

            if (entry.State == EntityState.Deleted || segment.State != SegmentState.Active)
            {
                rows.Remove(segment.Id);
            }
            else
            {
                rows[segment.Id] = segment;
            }
        }

        var stored = rows.Values.OrderBy(segment => segment.StartTicks).ThenBy(segment => segment.EndTicks).ToList();
        var submitted = result.Segments
            .Where(segment => segment.Decision == CandidateDecision.Accept)
            .ToList();
        var segments = stored.Select(segment =>
        {
            var priorCandidate = segment.Source == SegmentSource.Remote
                ? submitted.FirstOrDefault(candidate => candidate.StartTicks == segment.StartTicks && candidate.EndTicks == segment.EndTicks)
                : null;
            return priorCandidate is null
                ? new SegmentCandidate
                {
                    CandidateId = segment.Id.ToString("D"),
                    Type = mode,
                    StartTicks = segment.StartTicks,
                    EndTicks = segment.EndTicks,
                    Decision = CandidateDecision.Accept,
                    ConfidenceTier = "Server",
                    Reasons = ["ServerAuthoritative", segment.Source.ToString()],
                }
                : priorCandidate with { StartTicks = segment.StartTicks, EndTicks = segment.EndTicks, Decision = CandidateDecision.Accept };
        }).ToList();
        var canonicalResult = result with
        {
            Status = segments.Count == 0 ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments,
            Segments = segments,
        };
        var canonical = new CanonicalResult
        {
            Result = canonicalResult,
            ResultRevision = row.Revision,
            Disabled = await db.DisabledItems.AnyAsync(disabled => disabled.ItemId == itemId, cancellationToken).ConfigureAwait(false),
            Authority = "Server",
        };
        row.CanonicalJson = WireJson.Write(canonical);
        return canonical;
    }

    private static async Task<CanonicalResult> UpsertCanonicalAsync(IntroSkipperDbContext db, AnalysisResult result, CancellationToken cancellationToken)
    {
        var row = await db.RemoteAnalysisMetadata.FindAsync([result.ItemId, (int)result.Mode], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new DbRemoteAnalysisMetadata { ItemId = result.ItemId, Mode = (int)result.Mode, Revision = 0 };
            db.RemoteAnalysisMetadata.Add(row);
        }

        row.Revision++;
        row.DetectorId = result.Detector.Id;
        row.ProfileHash = result.Detector.ProfileHash;
        row.UpstreamCommit = result.Detector.UpstreamCommit;
        row.UpdatedUtc = DateTime.UtcNow;
        return (await CurrentCanonicalAsync(db, result.ItemId, result.Mode, result, cancellationToken).ConfigureAwait(false))!;
    }

    private sealed record ReviewSelection(List<ReviewDecision> Decisions, bool AcceptNoSegments);
}
