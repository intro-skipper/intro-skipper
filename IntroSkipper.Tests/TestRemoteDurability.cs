// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Db;
using SegmentScanner.Contracts;
using Xunit;

/// <summary>
/// Focused durability tests for remote analysis state.
/// </summary>
public sealed class TestRemoteDurability : IDisposable
{
    private readonly TempSegmentDb _db = new();

    /// <inheritdoc />
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ClaimRemoteJobAsync_ReturnsLeaseExpiryAsUtc()
    {
        var database = _db.Database;
        var registered = await database.HelloRemoteNodeAsync(new NodeHello("worker", "Worker", new NodeRuntimeInformation()), default);
        var enabled = Assert.IsType<RemoteNodeInfo>(await database.UpdateNodePolicyAsync(registered.NodeId, new NodePolicyUpdate(registered.Policy!, true), default));
        var snapshot = new JobSnapshot
        {
            JobId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            NodeId = enabled.NodeId,
            NodeRevision = enabled.Revision,
            Modes = [RemoteAnalysisMode.Introduction],
            Items = [new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "media", SourcePath = "/media/episode.mkv", FileVersionToken = "0" }],
        };
        Assert.True(await database.QueueRemoteJobAsync(snapshot, default));

        var lease = Assert.IsType<JobSnapshot>(await database.ClaimRemoteJobAsync(enabled.NodeId, TimeSpan.FromMinutes(1), default));
        var expiry = Assert.IsType<DateTimeOffset>(lease.LeaseExpiresUtc);
        await using var db = _db.Context();
        var storedExpiry = Assert.IsType<DateTime>(db.RemoteJobs.Single(job => job.Id == lease.JobId).LeaseExpiresUtc);

        Assert.Equal(DateTimeKind.Unspecified, storedExpiry.Kind);
        Assert.Equal(storedExpiry.Ticks, expiry.UtcDateTime.Ticks);
        Assert.Equal(TimeSpan.Zero, expiry.Offset);
    }

    [Fact]
    public async Task RebuildDatabaseAsync_PreservesAllRemoteDurableState()
    {
        var database = _db.Database;
        await database.GetSegmentsAsync(Guid.NewGuid());

        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        await using (var db = _db.Context())
        {
            db.RemoteNodes.Add(new DbRemoteNode
            {
                NodeId = "worker",
                ScopeJson = "{\"nodeId\":\"worker\"}",
                PolicyJson = "{\"concurrentFiles\":2}",
                RuntimeJson = "{\"version\":\"1.0\"}",
                Enabled = true,
                Revision = 2,
                UpdatedUtc = now,
                LastSeenUtc = now,
            });
            db.RemoteJobs.Add(new DbRemoteJob
            {
                Id = jobId,
                AttemptId = Guid.NewGuid(),
                SnapshotJson = "{\"jobId\":\"remote-job\"}",
                SnapshotHash = "snapshot-hash",
                State = "Leased",
                WorkerId = "worker",
                NodeId = "worker",
                NodeRevision = 2,
                LeaseTokenHash = "lease-hash",
                LeaseGeneration = 3,
                LeaseExpiresUtc = now.AddMinutes(5),
                Attempts = 4,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            db.RemoteImportReceipts.Add(new DbRemoteImportReceipt
            {
                JobId = jobId,
                AnalysisId = analysisId,
                ItemId = itemId,
                Mode = (int)RemoteAnalysisMode.Introduction,
                PayloadHash = "payload-hash",
                Outcome = "Applied",
                Reason = "reason",
                OriginalCanonicalJson = "{\"segments\":[]}",
                CreatedUtc = now,
            });
            db.RemoteAnalysisMetadata.Add(new DbRemoteAnalysisMetadata
            {
                ItemId = itemId,
                Mode = (int)RemoteAnalysisMode.Introduction,
                Revision = 5,
                CanonicalJson = "{\"segments\":[]}",
                DetectorId = "detector",
                ProfileHash = "profile-hash",
                UpstreamCommit = "commit",
                UpdatedUtc = now,
            });
            db.RemoteExportContexts.Add(new DbRemoteExportContext
            {
                Id = contextId,
                SnapshotJson = "{\"context\":true}",
                SnapshotHash = "context-hash",
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        await database.RebuildDatabaseAsync();

        await using var rebuilt = _db.Context();
        var node = Assert.Single(rebuilt.RemoteNodes);
        Assert.Equal("worker", node.NodeId);
        Assert.Equal("{\"nodeId\":\"worker\"}", node.ScopeJson);
        Assert.Equal("{\"concurrentFiles\":2}", node.PolicyJson);
        Assert.Equal("{\"version\":\"1.0\"}", node.RuntimeJson);
        Assert.True(node.Enabled);
        Assert.Equal(2L, node.Revision);
        var job = Assert.Single(rebuilt.RemoteJobs);
        Assert.Equal(jobId, job.Id);
        Assert.Equal("worker", job.NodeId);
        Assert.Equal(2L, job.NodeRevision);
        Assert.Equal("Leased", job.State);
        Assert.Equal("{\"jobId\":\"remote-job\"}", job.SnapshotJson);
        Assert.Equal("lease-hash", job.LeaseTokenHash);
        Assert.Equal(3, job.LeaseGeneration);
        Assert.Equal(4, job.Attempts);
        Assert.Equal("snapshot-hash", job.SnapshotHash);
        Assert.Equal(now.AddMinutes(5).Ticks, job.LeaseExpiresUtc!.Value.Ticks);
        var receipt = Assert.Single(rebuilt.RemoteImportReceipts);
        Assert.Equal(jobId, receipt.JobId);
        Assert.Equal(analysisId, receipt.AnalysisId);
        Assert.Equal(itemId, receipt.ItemId);
        Assert.Equal("Applied", receipt.Outcome);
        Assert.Equal("payload-hash", receipt.PayloadHash);
        Assert.Equal("reason", receipt.Reason);
        Assert.Equal("{\"segments\":[]}", receipt.OriginalCanonicalJson);
        var metadata = Assert.Single(rebuilt.RemoteAnalysisMetadata);
        Assert.Equal(itemId, metadata.ItemId);
        Assert.Equal(5L, metadata.Revision);
        Assert.Equal("{\"segments\":[]}", metadata.CanonicalJson);
        Assert.Equal("detector", metadata.DetectorId);
        Assert.Equal("profile-hash", metadata.ProfileHash);
        Assert.Equal("commit", metadata.UpstreamCommit);
        var context = Assert.Single(rebuilt.RemoteExportContexts);
        Assert.Equal(contextId, context.Id);
        Assert.Equal("{\"context\":true}", context.SnapshotJson);
        Assert.Equal("context-hash", context.SnapshotHash);
        Assert.Equal(now.AddMinutes(10).Ticks, context.ExpiresUtc!.Value.Ticks);
    }
}
