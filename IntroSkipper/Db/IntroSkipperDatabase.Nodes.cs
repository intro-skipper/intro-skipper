// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using IntroSkipper.Configuration;
using Microsoft.EntityFrameworkCore;
using SegmentScanner.Contracts;

namespace IntroSkipper.Db;

/// <summary>
/// Server-owned scanner-node policy and worker-owned runtime persistence.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <summary>Stable id of the virtual in-server scanner node.</summary>
    public const string LocalNodeId = "__introskipper_local__";

    internal async Task<IReadOnlyList<Guid>> GetPendingRemoteItemIdsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var snapshots = await db.RemoteJobs
            .Where(job => job.State == "Pending" || job.State == "Leased")
            .Select(job => job.SnapshotJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshots
            .Select(WireJson.Read<JobSnapshot>)
            .SelectMany(snapshot => snapshot.Items)
            .Where(item => item.WriteTarget)
            .Select(item => item.ItemId)
            .Distinct()
            .ToList();
    }

    internal async Task<RemoteNodeInfo> HelloRemoteNodeAsync(NodeHello hello, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
        var now = DateTime.UtcNow;
        var node = await db.RemoteNodes.FindAsync([hello.NodeId], cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            node = new DbRemoteNode
            {
                NodeId = hello.NodeId,
                ScopeJson = WireJson.Write(new RemoteNodeRegistration { NodeId = hello.NodeId, Name = hello.Name }),
                PolicyJson = WireJson.Write(DefaultPolicy(hello.Runtime)),
                RuntimeJson = WireJson.Write(hello.Runtime),
                Enabled = false,
                Revision = 1,
                UpdatedUtc = now,
                LastSeenUtc = now,
            };
            db.RemoteNodes.Add(node);
        }
        else
        {
            var scope = ToRegistration(node);
            if (!string.Equals(scope.Name, hello.Name, StringComparison.Ordinal))
            {
                node.ScopeJson = WireJson.Write(scope with { Name = hello.Name });
            }

            if (node.PolicyJson is null)
            {
                node.PolicyJson = WireJson.Write(NormalizePolicy(new RemoteNodePolicy
                {
                    LibraryIds = scope.LibraryIds,
                    Modes = scope.Modes,
                    CreditsMethod = scope.CreditsMethod,
                    UseGpu = hello.Runtime.CreditsRecipes
                        .FirstOrDefault(recipe => recipe.Method == scope.CreditsMethod && recipe.ProfileHash == scope.CreditsProfileHash)?
                        .UseGpu ?? false,
                    ConcurrentFiles = NormalizeWorkers(hello.Runtime.SuggestedConcurrentFiles),
                    PathMappings = hello.Runtime.SuggestedPathMappings
                        .Where(mapping => scope.ServerPaths.Any(path => string.Equals(NodeScope.NormalizeAbsolutePath(path.Prefix), NodeScope.NormalizeAbsolutePath(mapping.ServerPrefix), StringComparison.Ordinal)))
                        .ToList(),
                    AutomaticImports = scope.AutomaticImports,
                    LeaseMinutes = scope.LeaseMinutes,
                }));
            }

            node.RuntimeJson = WireJson.Write(hello.Runtime);
            node.LastSeenUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToNodeInfo(node);
        }
    }

    internal async Task<RemoteNodeInfo?> UpdateNodePolicyAsync(string nodeId, NodePolicyUpdate update, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
        var node = await db.RemoteNodes.FindAsync([nodeId], cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            if (nodeId != LocalNodeId)
            {
            return null;
            }

            node = new DbRemoteNode
            {
                NodeId = LocalNodeId,
                ScopeJson = WireJson.Write(new RemoteNodeRegistration { NodeId = LocalNodeId, Name = "IntroSkipper" }),
                RuntimeJson = WireJson.Write(LocalRuntime()),
                Revision = 0,
                UpdatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            };
            db.RemoteNodes.Add(node);
        }

        var json = WireJson.Write(NormalizePolicy(update.Policy));
        if (!string.Equals(node.PolicyJson, json, StringComparison.Ordinal) || node.Enabled != update.Enabled)
        {
            node.PolicyJson = json;
            node.Enabled = update.Enabled;
            node.Revision++;
            node.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return node.NodeId == LocalNodeId ? ToNodeInfo(node) with { Runtime = LocalRuntime(), IsLocal = true } : ToNodeInfo(node);
        }
    }

    internal async Task<RemoteNodeInfo> GetLocalNodeAsync(IReadOnlyCollection<Guid> catalogLibraryIds, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var node = await db.RemoteNodes.FindAsync([LocalNodeId], cancellationToken).ConfigureAwait(false);
        if (node is not null)
        {
            return ToNodeInfo(node) with { Runtime = LocalRuntime(), IsLocal = true };
        }

        // Until Nodes is saved, retain the local scanner's existing configuration.
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var policy = new RemoteNodePolicy
        {
            LibraryIds = catalogLibraryIds.Order().ToList(),
            Modes =
            [
                .. config.ScanIntroduction ? [RemoteAnalysisMode.Introduction] : Array.Empty<RemoteAnalysisMode>(),
                .. config.ScanCredits ? [RemoteAnalysisMode.Credits] : Array.Empty<RemoteAnalysisMode>(),
                .. config.ScanRecap ? [RemoteAnalysisMode.Recap] : Array.Empty<RemoteAnalysisMode>(),
                .. config.ScanPreview ? [RemoteAnalysisMode.Preview] : Array.Empty<RemoteAnalysisMode>(),
                .. config.ScanCommercial ? [RemoteAnalysisMode.Commercial] : Array.Empty<RemoteAnalysisMode>(),
            ],
            CreditsMethod = CreditsDetectionMethod.IntroSkipper,
            ConcurrentFiles = Math.Max(1, config.MaxParallelism),
        };
        return new RemoteNodeInfo
        {
            NodeId = LocalNodeId,
            Name = "IntroSkipper",
            LibraryIds = policy.LibraryIds,
            Modes = policy.Modes,
            CreditsMethod = CreditsDetectionMethod.IntroSkipper,
            Enabled = true,
            Revision = 0,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Policy = policy,
            Runtime = LocalRuntime(),
            IsLocal = true,
        };
    }

    private static RemoteNodePolicy DefaultPolicy(NodeRuntimeInformation runtime)
        => NormalizePolicy(new RemoteNodePolicy
        {
            Modes = [RemoteAnalysisMode.Introduction, RemoteAnalysisMode.Credits, RemoteAnalysisMode.Recap, RemoteAnalysisMode.Preview, RemoteAnalysisMode.Commercial],
            CreditsMethod = runtime.CreditsRecipes.Any(recipe => recipe.Method == CreditsDetectionMethod.TextAware && !recipe.UseGpu) ? CreditsDetectionMethod.TextAware : CreditsDetectionMethod.IntroSkipper,
            ConcurrentFiles = NormalizeWorkers(runtime.SuggestedConcurrentFiles),
            PathMappings = runtime.SuggestedPathMappings,
        });

    private static RemoteNodePolicy NormalizePolicy(RemoteNodePolicy policy)
        => policy with
        {
            LibraryIds = policy.LibraryIds.Distinct().Order().ToList(),
            Modes = policy.Modes.Distinct().Order().ToList(),
            PathMappings = policy.PathMappings
                .Select(mapping => mapping with { ServerPrefix = NodeScope.NormalizeAbsolutePath(mapping.ServerPrefix) })
                .OrderBy(mapping => mapping.ServerPrefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mapping => mapping.WorkerPrefix, StringComparer.Ordinal)
                .ThenBy(mapping => mapping.CaseSensitive)
                .ToList(),
        };

    private static int NormalizeWorkers(int workers)
        => workers is 1 or 2 or 4 ? workers : 2;

    private static NodeRuntimeInformation LocalRuntime()
    {
        var cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (string.IsNullOrWhiteSpace(cpuName) && OperatingSystem.IsLinux())
        {
            try
            {
                cpuName = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?
                    .Split(':', 2)[^1]
                    .Trim();
            }
            catch (IOException)
            {
                // Hardware reporting is advisory; unavailable platform details stay null.
            }
        }

        var modes = new List<RemoteAnalysisMode>
        {
            RemoteAnalysisMode.Introduction,
            RemoteAnalysisMode.Credits,
            RemoteAnalysisMode.Recap,
            RemoteAnalysisMode.Preview,
            RemoteAnalysisMode.Commercial,
        };
        return new NodeRuntimeInformation
        {
            Version = typeof(IntroSkipperDatabase).Assembly.GetName().Version?.ToString() ?? string.Empty,
            SupportedModes = modes,
            Hardware = new NodeHardwareInformation
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                CpuName = cpuName,
                LogicalProcessors = Environment.ProcessorCount,
                MemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes > 0 ? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes : null,
            },
        };
    }
}
