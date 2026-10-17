// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using System.Text;
using System.Text.Json;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Remote;
using IntroSkipper.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SegmentScanner.Contracts;

namespace IntroSkipper.Controllers;

/// <summary>Provides operator visibility and controls over local and remote scan processing.</summary>
/// <param name="database">Durable remote job facade.</param>
/// <param name="scheduler">Local analysis queue.</param>
/// <param name="activity">Current local file operations.</param>
/// <param name="progress">Lease-validated remote activity.</param>
/// <param name="library">Server media metadata.</param>
/// <param name="notifier">Connected node notifications.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper/Queue")]
public sealed class QueueController(IntroSkipperDatabase database, AnalysisScheduler scheduler, ScannerActivityTracker activity,
    ScannerQueueProgressStore progress, ILibraryManager library, NodeSettingsNotifier notifier) : ControllerBase
{
    private static readonly SemaphoreSlim ControlGate = new(1, 1);

    /// <summary>Returns active operations and up to 100 waiting batches.</summary>
    /// <param name="token">Request cancellation.</param>
    /// <returns>The current queue snapshot.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ScannerQueueSnapshot), 200)]
    public async Task<IActionResult> GetQueue(CancellationToken token) => Wire(await SnapshotAsync(token).ConfigureAwait(false));

    /// <summary>Starts, pauses, stops active processing or clears waiting work.</summary>
    /// <param name="token">Request cancellation.</param>
    /// <returns>The updated queue.</returns>
    [HttpPost("control")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(typeof(ScannerQueueSnapshot), 200)]
    public async Task<IActionResult> Control(CancellationToken token)
    {
        ScannerQueueControlRequest? request;
        try
        {
            request = await Request.ReadFromJsonAsync<ScannerQueueControlRequest>(WireJson.Options, token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return BadRequest("Invalid queue control request.");
        }

        if (request?.Action is not ("start" or "pause" or "stop" or "clear"))
        {
            return BadRequest("Choose start, pause, stop or clear.");
        }

        await ControlGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (request.Action == "clear")
            {
                await database.CancelRemoteQueueAsync(running: false, CancellationToken.None).ConfigureAwait(false);
                scheduler.ClearPending();
            }
            else
            {
                var plugin = Plugin.Instance!;
                plugin.Configuration.AnalysisPaused = request.Action != "start";
                plugin.UpdateConfiguration(plugin.Configuration);
                scheduler.RefreshControlState();
                if (request.Action == "stop")
                {
                    await database.CancelRemoteQueueAsync(running: true, CancellationToken.None).ConfigureAwait(false);
                    await scheduler.CancelRunningAsync().ConfigureAwait(false);
                }
            }

            notifier.Publish(reschedule: false);
            return Wire(await SnapshotAsync(token).ConfigureAwait(false));
        }
        finally
        {
            ControlGate.Release();
        }
    }

    private async Task<ScannerQueueSnapshot> SnapshotAsync(CancellationToken token)
    {
        var nodes = await database.GetRemoteNodesAsync(token).ConfigureAwait(false);
        var local = await database.GetLocalNodeAsync([], token).ConfigureAwait(false);
        var rows = await database.GetRemoteQueueAsync(token).ConfigureAwait(false);
        List<ScannerQueueJob> running = [];
        List<ScannerQueueJob> pending = [];
        var media = new Dictionary<Guid, ScannerQueueItem>();
        ScannerQueueItem Item(Guid id, string? fallback = null)
        {
            if (media.TryGetValue(id, out var known))
            {
                return known;
            }

            var item = library.GetItemById(id);
            var owner = item is Episode episode ? library.GetItemById(episode.SeriesId) ?? item : item;
            var libraryName = owner is null ? null : library.GetCollectionFolders(owner).FirstOrDefault()?.Name;
            var series = item is Episode e ? e.SeriesName : item is Season s ? library.GetItemById(s.SeriesId)?.Name : null;
            var name = item is Episode { ParentIndexNumber: { } seasonNumber, IndexNumber: { } episodeNumber }
                ? $"S{seasonNumber:00}E{episodeNumber:00} - {item.Name}"
                : item?.Name ?? Path.GetFileName(fallback) ?? id.ToString("D");
            return media[id] = new ScannerQueueItem(id, name, series, libraryName);
        }

        ScannerQueueJob Local(ScannerLocalQueueRequest request, string state)
        {
            var active = state == "Running" ? activity.Active.ToList() : [];
            var ids = request.ItemIds.Concat(active.Select(item => item.ItemId)).Distinct();
            var key = request.ItemIds.Count == 0 ? Guid.Empty : request.ItemIds[0];
            return new ScannerQueueJob($"local:{request.Kind}:{key:D}", IntroSkipperDatabase.LocalNodeId, local.Name, state, request.Kind, local.Modes, ids.Select(id => Item(id)).ToList(), active, [], null);
        }

        if (scheduler.RunningRequest is { } current)
        {
            running.Add(Local(current, "Running"));
        }

        var localPending = scheduler.PendingRequests;
        pending.AddRange(localPending.Take(100).Select(request => Local(request, "Pending")));
        foreach (var row in rows.Jobs)
        {
            var snapshot = WireJson.Read<JobSnapshot>(row.SnapshotJson);
            var isRunning = row.State == "Leased" && row.LeaseExpiresUtc > DateTime.UtcNow;
            var report = isRunning ? progress.Get(row.Id, row.LeaseGeneration) : null;
            var job = new ScannerQueueJob(row.Id.ToString("D"), row.NodeId ?? string.Empty, nodes.FirstOrDefault(node => node.NodeId == row.NodeId)?.Name ?? row.NodeId ?? "Remote node", isRunning ? "Running" : "Pending", "Remote analysis", snapshot.Modes, snapshot.Items.Select(item => Item(item.ItemId, item.SourcePath)).ToList(), report?.Active ?? [], report?.Completed ?? [], report?.UpdatedUtc);
            if (isRunning)
            {
                running.Add(job);
            }
            else if (pending.Count < 100)
            {
                pending.Add(job);
            }
        }

        return new ScannerQueueSnapshot(Plugin.Instance?.Configuration.AnalysisPaused ?? false, running, pending, rows.PendingCount + localPending.Count);
    }

    private ContentResult Wire<T>(T value) => Content(WireJson.Write(value), MediaTypeNames.Application.Json, Encoding.UTF8);
}
