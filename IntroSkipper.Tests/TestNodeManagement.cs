// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Remote;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SegmentScanner.Contracts;
using Xunit;

/// <summary>Server ownership and notification contracts for node configuration.</summary>
public sealed class NodeManagementTests
{
    [Fact]
    public async Task PolicyMigration_PreservesLegacyDeclarationThroughUpgradeAndRollback()
    {
        using var temporary = new TempSegmentDb();
        using var db = temporary.Context();
        var migrator = db.Database.GetService<IMigrator>();
        const string previous = "20261014131527_AddRegisteredRemoteNodes";
        await migrator.MigrateAsync(previous);
        var scope = WireJson.Write(new RemoteNodeRegistration { NodeId = "legacy-node", Name = "Existing PC", AutomaticImports = true });
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RemoteNodes (NodeId, ScopeJson, Enabled, Revision, UpdatedUtc, LastSeenUtc) VALUES ({"legacy-node"}, {scope}, {true}, {7L}, {now}, {now})");
        await migrator.MigrateAsync();
        var upgraded = await db.RemoteNodes.AsNoTracking().SingleAsync();
        Assert.Equal(scope, upgraded.ScopeJson);
        Assert.True(upgraded.Enabled);
        Assert.Equal(7, upgraded.Revision);
        Assert.Null(upgraded.PolicyJson);
        Assert.Null(upgraded.RuntimeJson);
        await migrator.MigrateAsync(previous);
        Assert.Equal(scope, await db.RemoteNodes.Select(node => node.ScopeJson).SingleAsync());
    }

    [Fact]
    public async Task Hello_RefreshesHardwareWithoutOverwritingSavedPolicyOrRevision()
    {
        using var harness = new SegmentChangeHarness();
        var runtime = new NodeRuntimeInformation
        {
            SuggestedPathMappings = [new PathMapping { ServerPrefix = "/media", WorkerPrefix = "I:/media" }],
        };
        var hello = new NodeHello("test-node", "Desktop", runtime);
        var registered = await harness.Database.HelloRemoteNodeAsync(hello, default);
        Assert.False(registered.Enabled);
        var library = Guid.NewGuid();
        var policy = new RemoteNodePolicy
        {
            LibraryIds = [library],
            Modes = [RemoteAnalysisMode.Recap, RemoteAnalysisMode.Preview, RemoteAnalysisMode.Commercial],
            PathMappings = runtime.SuggestedPathMappings,
            ConcurrentFiles = 4,
        };
        var saved = await harness.Database.UpdateNodePolicyAsync(hello.NodeId, new NodePolicyUpdate(policy, true), default);
        Assert.NotNull(saved);
        var refreshed = await harness.Database.HelloRemoteNodeAsync(hello with
        {
            Runtime = runtime with
            {
                SuggestedPathMappings = [new PathMapping { ServerPrefix = "/other", WorkerPrefix = "Z:/other" }],
                SuggestedConcurrentFiles = 1,
                Hardware = new NodeHardwareInformation { CpuName = "Updated CPU", LogicalProcessors = 8 },
            },
        }, default);
        Assert.True(refreshed.Enabled);
        Assert.Equal(saved.Revision, refreshed.Revision);
        Assert.Equal(WireJson.Hash(saved.Policy), WireJson.Hash(refreshed.Policy));
        Assert.Equal("Updated CPU", refreshed.Runtime!.Hardware.CpuName);
        var unchanged = await harness.Database.UpdateNodePolicyAsync(hello.NodeId, new NodePolicyUpdate(policy, true), default);
        Assert.Equal(saved.Revision, unchanged!.Revision);
        var persisted = await harness.Database.GetRemoteNodeAsync(hello.NodeId, default);
        Assert.InRange((DateTimeOffset.UtcNow - persisted!.LastSeenUtc!.Value).TotalSeconds, 0, 90);
    }

    [Theory]
    [InlineData(RemoteAnalysisMode.Introduction, Data.AnalysisMode.Introduction)]
    [InlineData(RemoteAnalysisMode.Credits, Data.AnalysisMode.Credits)]
    [InlineData(RemoteAnalysisMode.Recap, Data.AnalysisMode.Recap)]
    [InlineData(RemoteAnalysisMode.Preview, Data.AnalysisMode.Preview)]
    [InlineData(RemoteAnalysisMode.Commercial, Data.AnalysisMode.Commercial)]
    public void Modes_KeepWireIdentityAcrossDifferentPluginEnumOrder(RemoteAnalysisMode wire, Data.AnalysisMode plugin)
    {
        Assert.Equal(plugin, RemoteModes.ToPlugin(wire));
        Assert.Equal(wire, RemoteModes.ToRemote(plugin));
    }

    [Fact]
    public async Task V3Claim_SkipsPreUpgradeSnapshotsAndClaimsFreshPolicySnapshot()
    {
        using var harness = new SegmentChangeHarness();
        var registered = await harness.Database.HelloRemoteNodeAsync(new NodeHello("upgrade-node", "Desktop", new NodeRuntimeInformation()), default);
        var enabled = await harness.Database.UpdateNodePolicyAsync(registered.NodeId, new NodePolicyUpdate(registered.Policy!, true), default);
        var legacy = new JobSnapshot
        {
            JobId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            NodeId = enabled!.NodeId,
            NodeRevision = enabled.Revision,
            Modes = [RemoteAnalysisMode.Introduction],
            Items = [new JobItem { ItemId = Guid.NewGuid(), MediaSourceKey = "media", SourcePath = "/media/episode.mkv", FileVersionToken = "0" }],
        };
        Assert.True(await harness.Database.QueueRemoteJobAsync(legacy, default));
        Assert.Null(await harness.Database.ClaimRemoteJobAsync(enabled.NodeId, TimeSpan.FromMinutes(1), default, requireNodePolicy: true));
        var fresh = legacy with { JobId = Guid.NewGuid(), NodePolicy = enabled.Policy, ScanSettings = RemoteAnalysisDispatcher.GetScanSettings(new PluginConfiguration()) };
        Assert.True(await harness.Database.QueueRemoteJobAsync(fresh, default));
        Assert.NotNull(await harness.Database.ClaimRemoteJobAsync(enabled.NodeId, TimeSpan.FromMinutes(1), default, requireNodePolicy: true));
    }

    [Fact]
    public async Task Notification_WakesAConnectedReaderAfterSave()
    {
        var notifier = new NodeSettingsNotifier();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = notifier.ListenAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.True(await reader.MoveNextAsync());
        var pending = reader.MoveNextAsync().AsTask();
        notifier.Publish();
        Assert.True(await pending);
    }

    [Fact]
    public void SharedSettings_IncludeAnalyzerInputsAndExcludeMachinePathsAndCredentials()
    {
        var settings = RemoteAnalysisDispatcher.GetScanSettings(new PluginConfiguration
        {
            SubtitleRecapPattern = "confirmed recap",
            UseLegacyBlackFrameAnalyzer = true,
            ProcessThreads = 3,
        });
        Assert.Equal("confirmed recap", settings.Configuration.GetProperty("subtitleRecapPattern").GetString());
        Assert.True(settings.Configuration.GetProperty("useLegacyBlackFrameAnalyzer").GetBoolean());
        Assert.Equal(3, settings.Configuration.GetProperty("processThreads").GetInt32());
        Assert.DoesNotContain(settings.Configuration.EnumerateObject(), property => property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("path", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("remote", StringComparison.OrdinalIgnoreCase));
    }
}
