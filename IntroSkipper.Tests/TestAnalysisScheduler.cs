// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// The analysis queue over the real analyzer, a fake library holding one season with one
/// episode on disk, and a fake clock. Every pass goes through the ffmpeg stub's version
/// check, which the harness can park on a gate to hold a pass in flight; a completed pass
/// leaves an analysis record for the episode.
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
        Assert.True(await h.IsAnalyzedAsync());
        Assert.Equal(1, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task ChangedItem_ThatWaitedBehindAPass_RunsAsSoonAsThePassEnds()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
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
        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);

        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);

        Assert.False(changed.IsCompleted);
        h.Time.Advance(AnalysisScheduler.QuietPeriod);
        await changed.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ManualScan_RunsAsItsOwnPassAheadOfALibraryPass_ErasingFirst()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var first = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ =>
        {
            h.Record("erase A");
            return Task.CompletedTask;
        });
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None);
        var second = h.Queue.ScanAsync(Guid.NewGuid(), [h.EpisodeId], _ =>
        {
            // A failed erase is logged and the scan still analyzes.
            h.Record("erase B");
            throw new InvalidOperationException("erase failed");
        });
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: true, ChangedItems: 0, ManualScans: 1, LibraryPending: true), h.Queue.Status);

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(first, second, library).WaitAsync(Timeout);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(["erase A", "analyze", "erase B", "analyze", "analyze"], h.Events);
    }

    [Fact]
    public async Task LibraryPass_WaitsForThePassInFlight_AbsorbsChangedItems_AndReportsProgress()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        var progress = new RecordingProgress();

        var library = h.Queue.RunLibraryAsync(progress, CancellationToken.None);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: true, ChangedItems: 1, ManualScans: 0, LibraryPending: true), h.Queue.Status);

        h.Ffmpeg.Gate.SetResult();

        await Task.WhenAll(scan, library, changed).WaitAsync(Timeout);
        Assert.Contains(100, progress.Values);
        Assert.Equal(2, h.Ffmpeg.VersionCheckCalls);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: false, ChangedItems: 0, ManualScans: 0, LibraryPending: false), h.Queue.Status);
    }

    [Fact]
    public async Task LibraryPass_Cancellation_StopsOnlyThatPass()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        using var cancellation = new CancellationTokenSource();
        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), cancellation.Token);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.WaitAsync(Timeout));
        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);
        Assert.Equal(2, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task LibraryRequest_CancelledWhileWaiting_ReturnsAtOnceAndIsDropped()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var library = h.Queue.RunLibraryAsync(new RecordingProgress(), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.WaitAsync(Timeout));
        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);
        await WaitUntilAsync(() => !h.Queue.Status.LibraryPending);
        Assert.Equal(1, h.Ffmpeg.VersionCheckCalls);
    }

    [Fact]
    public async Task RepeatedRequests_MergeAndCompleteTogether()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var holding = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);

        var otherKey = Guid.NewGuid();
        var firstScan = h.Queue.ScanAsync(otherKey, [h.EpisodeId], _ => Task.CompletedTask);
        var secondScan = h.Queue.ScanAsync(otherKey, [h.EpisodeId], _ => Task.CompletedTask);
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

        var failed = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Timeout));
        Assert.Equal("ffmpeg exploded", exception.Message);

        await h.Queue.ScanAsync(Guid.NewGuid(), [h.EpisodeId], _ => Task.CompletedTask).WaitAsync(Timeout);
        Assert.True(await h.IsAnalyzedAsync());
    }

    [Fact]
    public async Task Status_ReportsRunningForAPendingScan_NotForAQuietPeriod()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        Assert.Equal(new AnalysisSchedulerStatus(PassRunning: false, ChangedItems: 0, ManualScans: 0, LibraryPending: false), h.Queue.Status);

        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);
        Assert.False(h.Queue.Status.IsRunning);
        Assert.Equal(1, h.Queue.Status.ChangedItems);

        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        Assert.True(h.Queue.Status.IsRunning);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        Assert.True(h.Queue.Status.PassRunning);

        h.Ffmpeg.Gate.SetResult();
        await scan.WaitAsync(Timeout);
        h.Time.Advance(AnalysisScheduler.QuietPeriod);
        await changed.WaitAsync(Timeout);
        await WaitUntilAsync(() => !h.Queue.Status.IsRunning);
    }

    [Fact]
    public async Task Stop_CancelsThePassInFlight_AndDropsPendingRequests()
    {
        await using var h = await QueueHarness.StartAsync(gated: true);
        var scan = h.Queue.ScanAsync(h.SeasonId, [h.EpisodeId], _ => Task.CompletedTask);
        await h.Ffmpeg.Entered.Task.WaitAsync(Timeout);
        var changed = h.Queue.ItemChangedAsync(h.EpisodeId);

        await h.Queue.StopAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changed.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queue.ItemChangedAsync(h.EpisodeId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queue.RunLibraryAsync(new RecordingProgress(), CancellationToken.None));
        Assert.False(await h.IsAnalyzedAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            lock (Values)
            {
                Values.Add(value);
            }
        }
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
        private readonly string _mediaPath;
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
            _mediaPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
            File.WriteAllText(_mediaPath, string.Empty);
            var episode = JellyfinItems.Episode(EpisodeId, SeriesId, SeasonId, path: _mediaPath);
            var library = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Shows")], JellyfinItems.WithParents(episode));
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);

            Ffmpeg = new GatedFfmpeg(gated)
            {
                VersionCheck = () =>
                {
                    Record("analyze");
                    return versionCheck is null ? false : versionCheck();
                },
            };
            Database = DatabaseTestHelpers.CreateTempSegmentDatabase();
            var cacheDbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
            var analyzer = new BaseItemAnalyzerTask(
                NullLoggerFactory.Instance,
                EntrypointTestHelpers.CreateSeasonResolver(library),
                Ffmpeg,
                DatabaseTestHelpers.CreateCacheService(cacheDbPath),
                DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath),
                Database);
            Queue = new AnalysisScheduler(analyzer, Time, NullLogger<AnalysisScheduler>.Instance);
        }

        public Guid SeriesId { get; } = Guid.NewGuid();

        public Guid SeasonId { get; } = Guid.NewGuid();

        public Guid EpisodeId { get; } = Guid.NewGuid();

        public FakeTimeProvider Time { get; } = new();

        public GatedFfmpeg Ffmpeg { get; }

        public AnalysisScheduler Queue { get; }

        public IntroSkipperDatabase Database { get; }

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

        public static async Task<QueueHarness> StartAsync(bool gated = false, Func<bool>? versionCheck = null)
        {
            var harness = new QueueHarness(gated, versionCheck);
            await harness.Queue.StartAsync(CancellationToken.None);
            return harness;
        }

        public void Record(string @event)
        {
            lock (_events)
            {
                _events.Add(@event);
            }
        }

        public async Task<bool> IsAnalyzedAsync()
        {
            var snapshot = await Database.GetSeasonQueueSnapshotAsync(SeasonId, [EpisodeId]);
            return snapshot.AnalysisRecords.ContainsKey((EpisodeId, AnalysisMode.Introduction));
        }

        public async ValueTask DisposeAsync()
        {
            await Queue.StopAsync(CancellationToken.None);
            Queue.Dispose();
            _scope.Dispose();
            File.Delete(_mediaPath);
        }
    }
}
