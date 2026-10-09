// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using IntroSkipper.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// The analysis queue over the real analyzer, a fake library holding one season with one
/// episode on disk, and a fake clock. Every pass that analyzes something goes through the
/// ffmpeg stub's version check, which the harness can park on a gate to hold a pass in
/// flight; a completed pass leaves an analysis record for the episode. A manual scan's
/// erase goes through a stub over the real eraser, so a test can fail or park it.
/// </summary>
public sealed class TestAnalysisScheduler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ChangedItem_RunsAfterTheQuietPeriod_AndANewChangeRestartsIt()
    {
        await using var h = await QueueHarness.StartAsync();

        var first = h.Queue.ItemChangedAsync(h.EpisodeId);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        var second = h.Queue.ItemChangedAsync(h.EpisodeId);
        h.Time.Advance(TimeSpan.FromSeconds(40));

        // 70 s after the first change, 40 s after the second: the window slid.
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(0, h.Ffmpeg.VersionCheckCalls);

        h.Time.Advance(TimeSpan.FromSeconds(21));

        await Task.WhenAll(first, second).WaitAsync(Timeout);
        Assert.True(await h.IsAnalyzedAsync(h.EpisodeId));
        Assert.Equal(1, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task ChangedItem_ThatWaitedBehindAPass_RunsAsSoonAsThePassEnds()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Scan();
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        h.Time.Advance(AnalysisScheduler.QuietPeriod + TimeSpan.FromSeconds(1));
        Assert.False(changed.IsCompleted);

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(scan, changed).WaitAsync(Timeout);
        Assert.Equal(2, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task ChangedItem_ArrivingDuringAPass_StillWaitsOutItsQuietPeriod()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Scan();
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);

        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);

        Assert.False(changed.IsCompleted);
        h.Time.Advance(AnalysisScheduler.QuietPeriod + TimeSpan.FromSeconds(1));
        await changed.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ManualScan_RunsAsItsOwnPassAheadOfALibraryPass_ErasingFirst()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        h.Time.Advance(AnalysisScheduler.QuietPeriod);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);
        var scan = h.Scan();
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: true, ChangedItems: 0, ManualScans: 1, LibraryPending: true), h.Queue.Status);

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(changed, scan, library).WaitAsync(Timeout);
        Assert.Equal(["analyze", "erase", "analyze", "analyze"], h.Events);
    }

    [Fact]
    public async Task ManualScan_RepeatedWhileItsPassRuns_RunsAgainAfterIt()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var first = h.Scan();
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        // The running pass may already have taken its inventory, so the repeat is a
        // follow-up pass of its own; a third request joins that pending one.
        var repeat = h.Scan();
        var third = h.Scan();
        Assert.Equal(1, h.Queue.Status.ManualScans);
        Assert.Equal(new ManualScanStatus(Queued: true, Failed: false), h.Queue.ScanStatus(h.SeasonId));

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(first, repeat, third).WaitAsync(Timeout);
        Assert.Equal(["erase", "analyze", "erase", "analyze"], h.Events);
        Assert.Equal(new ManualScanStatus(Queued: false, Failed: false), h.Queue.ScanStatus(h.SeasonId));
    }

    [Fact]
    public async Task ManualScan_ResolvesTheSeasonWhenItsPassStarts()
    {
        await using var h = QueueHarness.Create();
        Assert.IsType<AcceptedResult>(h.Controller.ScanSeason(h.SeriesId, h.SeasonId));

        // An episode added to the season after the scan was requested, with a segment of
        // its own; a second request for the same season joins the pending scan.
        var added = JellyfinItems.Episode(Guid.NewGuid(), h.SeriesId, h.SeasonId, episodeNumber: 2, path: h.MediaPath);
        h.Items.Add(added);
        await h.Segments.Database.ReplaceAutoSegmentsAsync(added.Id, AnalysisMode.Introduction, [new Segment(added.Id, new TimeRange(10, 20))], SegmentSource.Chapter);
        Assert.IsType<AcceptedResult>(h.Controller.ScanSeason(h.SeriesId, h.SeasonId));
        Assert.Equal(1, h.Queue.Status.ManualScans);

        var drained = h.Drain();
        await h.Queue.StartAsync(CancellationToken.None);
        await drained.WaitAsync(Timeout);

        Assert.Empty(await h.Segments.Database.GetSegmentsAsync(added.Id));
        Assert.True(await h.IsAnalyzedAsync(added.Id));
    }

    [Fact]
    public async Task ManualScan_OfASeasonThatNoLongerResolves_CompletesWithoutRunning()
    {
        await using var h = await QueueHarness.StartAsync();

        await h.Queue.ScanAsync(Guid.NewGuid()).WaitAsync(Timeout);

        Assert.Equal(0, h.Eraser.Erases);
        Assert.Equal(0, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task ManualScan_WhoseEraseFails_Faults_AndTheWorkerKeepsServing()
    {
        await using var h = await QueueHarness.StartAsync();
        var erases = 0;
        h.Eraser.OnErase = _ => ++erases == 1 ? throw new InvalidOperationException("erase failed") : Task.CompletedTask;

        var failed = h.Scan();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Timeout));
        Assert.Equal("erase failed", exception.Message);

        // Nothing was erased, so nothing was analyzed over the records still in place,
        // and the failure is remembered for the dashboard until a scan of the key completes.
        Assert.Equal(0, h.Ffmpeg.VersionCheckCalls);
        Assert.Equal(new ManualScanStatus(Queued: false, Failed: true), h.Queue.ScanStatus(h.SeasonId));

        await h.Scan().WaitAsync(Timeout);
        Assert.Equal(new ManualScanStatus(Queued: false, Failed: false), h.Queue.ScanStatus(h.SeasonId));
        Assert.True(await h.IsAnalyzedAsync(h.EpisodeId));
    }

    [Fact]
    public async Task ManualScan_FollowUpQueuedBehindAFailingScan_ReportsItsOwnResult()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var eraseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eraseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var erases = 0;
        h.Eraser.OnErase = async _ =>
        {
            if (++erases > 1)
            {
                return;
            }

            eraseEntered.SetResult();
            await eraseGate.Task;
            throw new InvalidOperationException("erase failed");
        };
        var failing = h.Scan();
        await eraseEntered.Task.WaitAsync(Timeout);
        var followUp = h.Scan();

        eraseGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Timeout));

        // The follow-up is parked at its analysis; the failure stands until it completes.
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        Assert.Equal(new ManualScanStatus(Queued: true, Failed: true), h.Queue.ScanStatus(h.SeasonId));

        h.Ffmpeg.Gate.SetResult();
        await followUp.WaitAsync(Timeout);
        Assert.Equal(new ManualScanStatus(Queued: false, Failed: false), h.Queue.ScanStatus(h.SeasonId));
    }

    [Fact]
    public async Task LibraryPass_WaitsForThePassInFlight_ReportsProgress_AndLeavesChangedItemsToTheirOwnPass()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Scan();
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        var progress = new RecordingProgress();

        var library = h.Queue.RunLibraryAsync(progress, CancellationToken.None);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: true, ChangedItems: 1, ManualScans: 0, LibraryPending: true), h.Queue.Status);

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(scan, library).WaitAsync(Timeout);
        Assert.Contains(100, progress.Values);

        // The library pass covered the item but did not take its request: it still owes
        // its quiet period and then runs a pass of its own, which finds it analyzed.
        Assert.False(changed.IsCompleted);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: false, ChangedItems: 1, ManualScans: 0, LibraryPending: false), h.Queue.Status);

        h.Time.Advance(AnalysisScheduler.QuietPeriod);

        await changed.WaitAsync(Timeout);
        Assert.Equal(3, h.Ffmpeg.VersionCheckCalls);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: false, ChangedItems: 0, ManualScans: 0, LibraryPending: false), h.Queue.Status);
    }

    [Fact]
    public async Task LibraryPass_Cancellation_StopsOnlyThatPass()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        using var cancellation = new CancellationTokenSource();
        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), cancellation.Token);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var scan = h.Scan();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.WaitAsync(Timeout));
        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);
        Assert.Equal(2, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task LibraryPass_Cancellation_ReturnsOnceThePassHasStopped_AndLeavesPendingChangesAlone()
    {
        await using var h = QueueHarness.Create(gated: true);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        using var cancellation = new CancellationTokenSource();
        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), cancellation.Token);
        await h.Queue.StartAsync(CancellationToken.None);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.WaitAsync(Timeout));

        // The caller returned after the pass stopped, not when it asked; the change is
        // still pending and still owes its quiet period from the change.
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: false, ChangedItems: 1, ManualScans: 0, LibraryPending: false), h.Queue.Status);
        Assert.False(changed.IsCompleted);

        h.Ffmpeg.Gate.SetResult();
        h.Time.Advance(TimeSpan.FromMinutes(2));

        await changed.WaitAsync(Timeout);
        Assert.Equal(2, h.Ffmpeg.VersionCheckCalls);
        Assert.True(await h.IsAnalyzedAsync(h.EpisodeId));
    }

    [Fact]
    public async Task LibraryRequest_CancelledWhileWaiting_IsWithdrawnAtOnce_AndARestartRuns()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Scan();
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var first = h.Queue.RunLibraryAsync(new RecordingProgress(), cancellation.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None));

        await cancellation.CancelAsync();

        // Withdrawn while the scan still holds the worker, so the restart inherits nothing.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
        Assert.False(h.Queue.Status.LibraryPending);
        var restarted = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);

        h.Ffmpeg.Gate.SetResult();
        await Task.WhenAll(scan, restarted).WaitAsync(Timeout);
        Assert.Equal(2, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task LibraryRequests_RegularAndShortcutCanWaitTogether_WithoutDroppingEither()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var holding = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        var shortcut = h.Queue.RunLibraryAsync(
            new RecordingProgress(),
            CancellationToken.None,
            shortcutsOnly: true,
            shortcutBatchSize: 1);
        var regular = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(holding, shortcut, regular).WaitAsync(Timeout);
        Assert.Equal(3, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task RepeatedRequests_MergeAndCompleteTogether()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var holding = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        var firstScan = h.Scan();
        var secondScan = h.Scan();
        var firstChange = h.Queue.ItemChangedAsync(h.EpisodeId);
        var secondChange = h.Queue.ItemChangedAsync(h.EpisodeId);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: true, ChangedItems: 1, ManualScans: 1, LibraryPending: false), h.Queue.Status);

        h.Time.Advance(AnalysisScheduler.QuietPeriod);
        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(holding, firstScan, secondScan, firstChange, secondChange).WaitAsync(Timeout);
        Assert.Equal(3, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task ThrowingPass_FaultsItsHandles_AndTheWorkerKeepsServing()
    {
        var calls = 0;
        await using var h = await QueueHarness.StartAsync(versionCheck: () => ++calls == 1 ? throw new InvalidOperationException("ffmpeg exploded") : false);

        var failed = h.Scan();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Timeout));
        Assert.Equal("ffmpeg exploded", exception.Message);

        await h.Scan().WaitAsync(Timeout);
        Assert.True(await h.IsAnalyzedAsync(h.EpisodeId));
    }

    [Fact]
    public async Task Status_ReportsRunningForAPendingScan_NotForAQuietPeriod()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: false, ChangedItems: 0, ManualScans: 0, LibraryPending: false), h.Queue.Status);

        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        Assert.False(h.Queue.Status.IsRunning);
        Assert.Equal(1, h.Queue.Status.ChangedItems);

        var scan = h.Scan();
        Assert.True(h.Queue.Status.IsRunning);
        Assert.True(h.Queue.ScanStatus(h.SeasonId).Queued);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        Assert.True(h.Queue.Status.PassRunning);
        Assert.True(h.Queue.ScanStatus(h.SeasonId).Queued);

        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);
        Assert.False(h.Queue.ScanStatus(h.SeasonId).Queued);
        h.Time.Advance(AnalysisScheduler.QuietPeriod);
        await changed.WaitAsync(Timeout);
        Assert.False(h.Queue.Status.IsRunning);
    }

    [Fact]
    public async Task Stop_CancelsThePassInFlight_AndDropsPendingRequests()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Scan();
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);

        await h.Queue.StopAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changed.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queue.ItemChangedAsync(h.EpisodeId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None));
        Assert.False(await h.IsAnalyzedAsync(h.EpisodeId));
    }

    [Fact]
    public async Task Stop_WithAnExpiredHostDeadline_StillCancelsTheDroppedRequests()
    {
        await using var h = await QueueHarness.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The erase ignores cancellation, so the pass outlives the host's shutdown budget.
        h.Eraser.OnErase = async _ =>
        {
            entered.SetResult();
            await gate.Task;
        };
        var holding = h.Scan();
        await entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        var pendingScan = h.Queue.ScanAsync(Guid.NewGuid());
        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queue.StopAsync(new CancellationToken(canceled: true)));

        // Every dropped handle settles while the pass still holds the worker.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changed.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingScan.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.WaitAsync(Timeout));

        gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holding.WaitAsync(Timeout));
    }

    // Parks every pass at its ffmpeg version check until the gate opens; a released gate
    // lets every later pass through.
    private sealed class GatedFfmpeg(bool gated) : StubFFmpegService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
        {
            var result = await base.CheckFFmpegVersionAsync(cancellationToken);
            if (gated)
            {
                Entered.TrySetResult();
                await Gate.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class QueueHarness : IAsyncDisposable
    {
        private readonly EntrypointTestHelpers.PluginInstanceScope _scope;
        private readonly List<string> _events = [];

        private QueueHarness(bool gated, Func<bool>? versionCheck)
        {
            var config = new PluginConfiguration
            {
                ScanIntroduction = true,
                ScanCredits = false,
                ScanRecap = false,
                ScanPreview = false,
                ScanCommercial = false,
            };
            _scope = EntrypointTestHelpers.CreatePluginScope(config, []);
            MediaPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
            File.WriteAllText(MediaPath, string.Empty);
            Items = [.. JellyfinItems.WithParents(JellyfinItems.Episode(EpisodeId, SeriesId, SeasonId, path: MediaPath))];
            var library = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Shows")], Items);
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);

            Ffmpeg = new GatedFfmpeg(gated)
            {
                VersionCheck = () =>
                {
                    Record("analyze");
                    return versionCheck is null ? false : versionCheck();
                },
            };
            var resolver = EntrypointTestHelpers.CreateSeasonResolver(library);
            Eraser = new StubSegmentEraser(new SegmentEraser(Segments.Database, DatabaseTestHelpers.CreateCacheDatabase(_scope.CacheDbPath), Segments.Change))
            {
                OnErase = _ =>
                {
                    Record("erase");
                    return Task.CompletedTask;
                },
            };
            Queue = EntrypointTestHelpers.CreateQueue(resolver, Ffmpeg, Segments.Database, _scope.CacheDbPath, Eraser, Time);
            Controller = new VisualizationController(NullLogger<VisualizationController>.Instance, Segments.Change, Queue, resolver, Segments.Database, Eraser);
        }

        public Guid SeriesId { get; } = Guid.NewGuid();

        public Guid SeasonId { get; } = Guid.NewGuid();

        public Guid EpisodeId { get; } = Guid.NewGuid();

        public string MediaPath { get; }

        /// <summary>Gets the library's items; the fake reads this list live, so tests can add to it.</summary>
        public List<BaseItem> Items { get; }

        public FakeTimeProvider Time { get; } = new();

        public GatedFfmpeg Ffmpeg { get; }

        /// <summary>Gets the manual scan's eraser: counts erases and runs its hook, which records "erase" unless a test replaces it, before erasing for real.</summary>
        public StubSegmentEraser Eraser { get; }

        public SegmentChangeHarness Segments { get; } = new();

        public AnalysisScheduler Queue { get; }

        public VisualizationController Controller { get; }

        /// <summary>Gets the erases and pass starts in the order they happened.</summary>
        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public static QueueHarness Create(bool gated = false, Func<bool>? versionCheck = null) => new(gated, versionCheck);

        public static async Task<QueueHarness> StartAsync(bool gated = false, Func<bool>? versionCheck = null)
        {
            var harness = Create(gated, versionCheck);
            await harness.Queue.StartAsync(CancellationToken.None);
            return harness;
        }

        /// <summary>Queues a manual scan of the harness season.</summary>
        public Task Scan() => Queue.ScanAsync(SeasonId);

        /// <summary>Queues a scan of a season that does not exist: it completes at its turn without running, marking that everything queued before it has run.</summary>
        public Task Drain() => Queue.ScanAsync(Guid.NewGuid());

        public void Record(string @event)
        {
            lock (_events)
            {
                _events.Add(@event);
            }
        }

        public async Task<bool> IsAnalyzedAsync(Guid episodeId)
        {
            var snapshot = await Segments.Database.GetSeasonQueueSnapshotAsync(SeasonId, [episodeId]);
            return snapshot.AnalysisRecords.ContainsKey((episodeId, AnalysisMode.Introduction));
        }

        public async ValueTask DisposeAsync()
        {
            await Queue.StopAsync(CancellationToken.None);
            Queue.Dispose();
            Segments.Dispose();
            _scope.Dispose();
            File.Delete(MediaPath);
        }
    }
}
