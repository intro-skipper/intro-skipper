// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.Remote;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using SegmentScanner.Contracts;
using Xunit;
using RemoteMode = SegmentScanner.Contracts.RemoteAnalysisMode;
using TimeRange = IntroSkipper.Data.TimeRange;

public sealed class TestRemoteScheduling
{
    [Fact]
    public async Task DependentSnapshots_IncludeCompletedLocalIntroductionAndCredits()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            EnableSponsorBlockChapterDetection = false,
        },
        [
            new ChapterInfo { Name = "Opening", StartPositionTicks = DatabaseTestHelpers.Ticks(5) },
            new ChapterInfo { Name = "Episode", StartPositionTicks = DatabaseTestHelpers.Ticks(30) },
            new ChapterInfo { Name = "Credits", StartPositionTicks = DatabaseTestHelpers.Ticks(200) },
        ]);
        using var media = new TempFile();
        using var db = new TempSegmentDb();
        var folder = JellyfinItems.Folder("Media");
        var libraryId = Guid.Parse(folder.ItemId);
        var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), path: media.Path);
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([folder], JellyfinItems.WithParents(episode), getCollectionFolders: _ => [new Folder { Id = libraryId }]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);
        await db.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "dependent", Name = "Dependent", LibraryIds = [libraryId],
            Modes = [RemoteMode.Recap, RemoteMode.Preview], ServerPaths = [new RemoteNodePath { Prefix = System.IO.Path.GetDirectoryName(media.Path)! }],
        }, default);
        await db.Database.SetRemoteNodeEnabledAsync("dependent", true, default);
        var ffmpeg = new StubFFmpegService { VersionCheck = () => false };
        var task = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, EntrypointTestHelpers.CreateSeasonResolver(library), ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(), null!, db.Database,
            new RemoteAnalysisDispatcher(db.Database, RemoteAnalysisBridgeTests.RemoteHostProxy.Create()));

        await task.AnalyzeItemsAsync(new Progress<double>(), default);

        var rows = (await db.Database.GetSegmentsAsync(episode.Id)).Where(row => row.Type is AnalysisMode.Introduction or AnalysisMode.Credits).ToList();
        Assert.Equal(2, rows.Count);
        for (var count = 0; count < 2; count++)
        {
            var job = await db.Database.ClaimRemoteJobAsync("dependent", TimeSpan.FromMinutes(1), default);
            Assert.NotNull(job);
            var contexts = Assert.Single(job.Items).ContextSegments!;
            Assert.All(rows, row => Assert.Contains(contexts, segment => segment.Mode == RemoteModes.ToRemote(row.Type)
                && segment.StartTicks == row.StartTicks && segment.EndTicks == row.EndTicks));
        }
    }

    [Fact]
    public async Task ManagedCreditsReanalysis_RefreshesAnAlreadySettledDerivedPreview()
    {
        var config = new PluginConfiguration
        {
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            EnableSponsorBlockChapterDetection = false,
            MinimumPreviewDuration = 10,
        };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config,
        [
            new ChapterInfo { Name = "Credits", StartPositionTicks = DatabaseTestHelpers.Ticks(180) },
            new ChapterInfo { Name = "Scene", StartPositionTicks = DatabaseTestHelpers.Ticks(210) },
        ]);
        using var media = new TempFile();
        using var db = new TempSegmentDb();
        var folder = JellyfinItems.Folder("Media");
        var libraryId = Guid.Parse(folder.ItemId);
        var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), path: media.Path);
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([folder], JellyfinItems.WithParents(episode), getCollectionFolders: _ => [new Folder { Id = libraryId }]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);
        await db.Database.SetAnalysisOverridesAsync(episode.SeasonId, null, null, previewFromCreditsEnd: true, subtitleRecapDetection: null, subtitlePreviewDetection: null);
        await db.Database.UpdateNodePolicyAsync(IntroSkipperDatabase.LocalNodeId, new NodePolicyUpdate(new RemoteNodePolicy
        {
            LibraryIds = [libraryId], Modes = [RemoteMode.Credits, RemoteMode.Preview], CreditsMethod = CreditsDetectionMethod.IntroSkipper,
        }, true), default);
        var previewHash = ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, false, previewFromCreditsEndOverride: true);
        await db.Database.ReplaceAutoSegmentsAsync(episode.Id, AnalysisMode.Credits, [new Segment(episode.Id, new TimeRange(180, 200))], SegmentSource.Chapter, "previous-credits-hash");
        await db.Database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [(episode.Id, SeasonResolver.FileVersion(episode))], "previous-credits-hash");
        await db.Database.ReplaceAutoSegmentsAsync(episode.Id, AnalysisMode.Preview, [new Segment(episode.Id, new TimeRange(200, 240))], SegmentSource.CreditsDerived, previewHash);
        await db.Database.MarkItemsAnalyzedAsync(AnalysisMode.Preview, [(episode.Id, SeasonResolver.FileVersion(episode))], previewHash);
        var ffmpeg = new StubFFmpegService { VersionCheck = () => false };
        var task = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, EntrypointTestHelpers.CreateSeasonResolver(library), ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(), null!, db.Database,
            new RemoteAnalysisDispatcher(db.Database, RemoteAnalysisBridgeTests.RemoteHostProxy.Create()));

        await task.AnalyzeItemsAsync(new Progress<double>(), default);

        var rows = await db.Database.GetSegmentsAsync(episode.Id);
        Assert.Equal(DatabaseTestHelpers.Ticks(210), Assert.Single(rows, row => row.Type == AnalysisMode.Credits).EndTicks);
        var preview = Assert.Single(rows, row => row.Type == AnalysisMode.Preview);
        Assert.Equal(DatabaseTestHelpers.Ticks(210), preview.StartTicks);
        Assert.Equal(previewHash, preview.ConfigHash);
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
    }

    [Fact]
    public async Task PendingRemoteContext_DefersOnlyItsPreviewAndDoesNotSettleTheSeason()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            ReanalyzeSettledSeasons = true,
            SettledSeasonDelayHours = 0,
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            EnableSponsorBlockChapterDetection = false,
        },
        [
            new ChapterInfo { Name = "Opening", StartPositionTicks = DatabaseTestHelpers.Ticks(5) },
            new ChapterInfo { Name = "Episode", StartPositionTicks = DatabaseTestHelpers.Ticks(30) },
            new ChapterInfo { Name = "Preview", StartPositionTicks = DatabaseTestHelpers.Ticks(200) },
        ]);
        using var firstMedia = new TempFile();
        using var otherMedia = new TempFile();
        using var db = new TempSegmentDb();
        var folder = JellyfinItems.Folder("Media");
        var libraryId = Guid.Parse(folder.ItemId);
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 3).Select(number => JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId,
            path: number == 1 ? firstMedia.Path : otherMedia.Path, episodeNumber: number)).ToArray();
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([folder], JellyfinItems.WithParents(episodes), getCollectionFolders: _ => [new Folder { Id = libraryId }]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);
        await db.Database.UpdateNodePolicyAsync(IntroSkipperDatabase.LocalNodeId, new NodePolicyUpdate(new RemoteNodePolicy
        {
            LibraryIds = [libraryId], Modes = [RemoteMode.Introduction, RemoteMode.Preview], CreditsMethod = CreditsDetectionMethod.IntroSkipper,
        }, true), default);
        await db.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "intro", Name = "Intro", LibraryIds = [libraryId], Modes = [RemoteMode.Introduction],
            ServerPaths = [new RemoteNodePath { Prefix = firstMedia.Path }],
        }, default);
        await db.Database.SetRemoteNodeEnabledAsync("intro", true, default);
        var task = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, EntrypointTestHelpers.CreateSeasonResolver(library), new StubFFmpegService { VersionCheck = () => false },
            DatabaseTestHelpers.CreateTempCacheService(), null!, db.Database,
            new RemoteAnalysisDispatcher(db.Database, RemoteAnalysisBridgeTests.RemoteHostProxy.Create()));

        await task.AnalyzeItemsAsync(new Progress<double>(), default);

        Assert.DoesNotContain(await db.Database.GetSegmentsAsync(episodes[0].Id), row => row.Type == AnalysisMode.Preview);
        foreach (var episode in episodes.Skip(1))
        {
            Assert.Contains(await db.Database.GetSegmentsAsync(episode.Id), row => row.Type == AnalysisMode.Preview);
        }

        var queue = await db.Database.GetSeasonQueueSnapshotAsync(seasonId, episodes.Select(item => item.Id).ToArray());
        Assert.False(queue.AnalysisRecords.ContainsKey((episodes[0].Id, AnalysisMode.Preview)));
        var states = await db.Database.GetSettleReanalysisStatesAsync(seasonId);
        Assert.DoesNotContain(AnalysisMode.Preview, states.Keys);
        Assert.NotNull(await db.Database.ClaimRemoteJobAsync("intro", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task SettledRemoteSeason_ReopensAllEpisodesAndKeepsDurableJobOnNextPass()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true, SettledSeasonDelayHours = 0 };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        using var media = new TempFile();
        using var db = new TempSegmentDb();
        var folder = JellyfinItems.Folder("Media");
        var libraryId = Guid.Parse(folder.ItemId);
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 3).Select(number => JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId, path: media.Path, episodeNumber: number)).ToArray();
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([folder], JellyfinItems.WithParents(episodes), getCollectionFolders: _ => [new Folder { Id = libraryId }]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);
        await db.Database.UpdateNodePolicyAsync(IntroSkipperDatabase.LocalNodeId, new NodePolicyUpdate(new RemoteNodePolicy { CreditsMethod = CreditsDetectionMethod.IntroSkipper }, false), default);
        await db.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "season", Name = "Season", LibraryIds = [libraryId], Modes = [RemoteMode.Introduction],
            ServerPaths = [new RemoteNodePath { Prefix = System.IO.Path.GetDirectoryName(media.Path)! }],
        }, default);
        await db.Database.SetRemoteNodeEnabledAsync("season", true, default);
        foreach (var episode in episodes)
        {
            await db.Database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(episode.Id, SeasonResolver.FileVersion(episode))],
                ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, true));
        }

        await db.Database.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], episodes.Take(2).Select(item => item.Id).ToArray());
        var task = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, EntrypointTestHelpers.CreateSeasonResolver(library), new StubFFmpegService(),
            DatabaseTestHelpers.CreateTempCacheService(), null!, db.Database,
            new RemoteAnalysisDispatcher(db.Database, RemoteAnalysisBridgeTests.RemoteHostProxy.Create()));

        await task.AnalyzeItemsAsync(new Progress<double>(), default);

        var job = await db.Database.ClaimRemoteJobAsync("season", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(job);
        Assert.Equal(episodes.Select(item => item.Id).Order(), job.Items.Where(item => item.WriteTarget).Select(item => item.ItemId).Order());
        await task.AnalyzeItemsAsync(new Progress<double>(), default);
        Assert.True(await db.Database.RenewRemoteLeaseAsync(job.JobId, job.LeaseToken!, job.LeaseGeneration, TimeSpan.FromMinutes(1), default));
        Assert.Null(await db.Database.ClaimRemoteJobAsync("season", TimeSpan.FromMinutes(1), default));
    }

    private sealed class TempFile : IDisposable
    {
        internal string Path { get; } = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");

        internal TempFile() => File.WriteAllText(Path, string.Empty);

        public void Dispose() => File.Delete(Path);
    }
}
