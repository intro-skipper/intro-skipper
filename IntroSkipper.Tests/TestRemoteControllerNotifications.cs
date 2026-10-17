// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.Remote;
using IntroSkipper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SegmentScanner.Contracts;
using Xunit;
using PluginMode = IntroSkipper.Data.AnalysisMode;
using RemoteMode = SegmentScanner.Contracts.RemoteAnalysisMode;

/// <summary>Controller contracts for remote-notification side effects.</summary>
public sealed class RemoteControllerNotificationsTests : IDisposable
{
    private const string ServerId = "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b";
    private readonly SegmentChangeHarness _harness = new();

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SetNodeEnabled_NotifiesConnectedWorkers()
    {
        var node = await _harness.Database.HelloRemoteNodeAsync(new NodeHello("notification-node", "Desktop", new NodeRuntimeInformation()), default);
        var notifier = new NodeSettingsNotifier();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = notifier.ListenAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.True(await reader.MoveNextAsync());
        var pending = reader.MoveNextAsync().AsTask();
        var controller = new RemoteController(_harness.Database, null!, null!, notifier)
        {
            ControllerContext = new ControllerContext { HttpContext = JsonContext(new NodeEnabledRequest(true)) },
        };

        Assert.IsType<ContentResult>(await controller.SetNodeEnabled(node.NodeId, default));

        Assert.True(await pending);
    }

    [Fact]
    public async Task AcceptedHeldIntroductionReview_QueuesDependentAnalysis()
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(itemId, seriesId, seasonId);
        var season = JellyfinItems.Season(seasonId, seriesId);
        var series = JellyfinItems.Series(seriesId);
        EntrypointTestHelpers.SetPropertyOrField(season, "ParentId", seriesId);
        EntrypointTestHelpers.SetPropertyOrField(series, "ParentId", libraryId);
        var configuration = new PluginConfiguration
        {
            RemoteAnalysisEnabled = true,
            RemoteAnalysisModes = [PluginMode.Introduction],
            RemoteAnalysisLibraryIds = [libraryId],
        };
        using var scope = EntrypointTestHelpers.CreatePluginScope(configuration);
        var items = new BaseItem[] { episode, season, series };
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.FakeLibraryManager.Create([], _ => [], id => items.FirstOrDefault(item => item.Id == id), getCollectionFolders: _ => [new Folder { Id = libraryId }]));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.Create(Array.Empty<ChapterInfo>()));

        var registered = await _harness.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "review-node",
            Name = "Review node",
            LibraryIds = [libraryId],
            Modes = [RemoteMode.Introduction],
            ServerPaths = [new RemoteNodePath { Prefix = "/media" }],
        }, default);
        var node = Assert.IsType<RemoteNodeInfo>(await _harness.Database.SetRemoteNodeEnabledAsync(registered.NodeId, true, default));
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(CultureInfo.InvariantCulture);
        var configHash = ConfigHasher.Analysis(configuration, PluginMode.Introduction, AnalyzerAction.Default, ffmpegValid: false);
        var snapshot = new JobSnapshot
        {
            JobId = Guid.NewGuid(),
            AttemptId = Guid.CreateVersion7(),
            ServerId = ServerId,
            NodeId = node.NodeId,
            NodeRevision = node.Revision,
            Modes = [RemoteMode.Introduction],
            IntroductionSettings = JsonSerializer.SerializeToElement(new { analysisPercent = 100, analysisLengthLimit = 10 }),
            IntroductionProfileHash = RemoteAnalysisDispatcher.GetIntroductionProfileHash(configuration),
            Items =
            [
                new JobItem
                {
                    ItemId = itemId,
                    MediaSourceKey = "/media/series/s01e01.mkv",
                    SourcePath = "/media/series/s01e01.mkv",
                    FileVersionToken = fileVersion,
                    ServerAnalysisConfigHash = configHash,
                    ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Introduction] = "0" },
                    SeasonKey = seasonId.ToString("D", CultureInfo.InvariantCulture),
                    RuntimeTicks = TimeSpan.FromMinutes(4).Ticks,
                    ChaptersAreAuthoritative = true,
                },
            ],
        };
        Assert.True(await _harness.Database.QueueRemoteJobAsync(snapshot, default));
        var lease = await _harness.Database.ClaimRemoteJobAsync(node.NodeId, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var held = new AnalysisResult
        {
            AnalysisId = Guid.CreateVersion7(),
            JobId = lease.JobId,
            AttemptId = lease.AttemptId,
            ServerId = lease.ServerId,
            ItemId = itemId,
            MediaSourceKey = "/media/series/s01e01.mkv",
            SourcePath = "/media/series/s01e01.mkv",
            FileVersionToken = fileVersion,
            ExpectedItemModeRevision = "0",
            Mode = RemoteMode.Introduction,
            Status = AnalysisStatus.NeedsReview,
            TimelineEndTicks = TimeSpan.FromMinutes(4).Ticks,
            Detector = new DetectorIdentity { Id = "test", ProfileHash = lease.IntroductionProfileHash, ProductionApproved = false },
            Segments =
            [
                new SegmentCandidate
                {
                    CandidateId = "candidate",
                    Type = RemoteMode.Introduction,
                    StartTicks = 100,
                    EndTicks = 200,
                    Decision = CandidateDecision.Review,
                },
            ],
        };
        Assert.Equal("Held", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(lease.JobId, new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration), [held], default)).Outcome);
        using var scheduler = EntrypointTestHelpers.CreateEntrypoint().Queue;
        var review = new RemoteReviewRequest(
            [held],
            new ReviewDocument { Decisions = [new ReviewDecision { AnalysisId = held.AnalysisId, CandidateId = "candidate", Decision = CandidateDecision.Accept }] });
        var controller = new RemoteController(_harness.Database, null!, null!, scheduler: scheduler)
        {
            ControllerContext = new ControllerContext { HttpContext = JsonContext(review) },
        };

        var response = Assert.IsType<ContentResult>(await controller.Review(lease.JobId, default));

        Assert.Equal("Accepted", Assert.Single(WireJson.Read<List<ImportReceipt>>(response.Content!)).Outcome);
        Assert.Equal(1, scheduler.Status.ChangedItems);
    }

    private static DefaultHttpContext JsonContext<T>(T value)
    {
        var payload = Encoding.UTF8.GetBytes(WireJson.Write(value));
        return new DefaultHttpContext
        {
            Request =
            {
                ContentType = "application/json",
                ContentLength = payload.Length,
                Body = new MemoryStream(payload),
            },
        };
    }
}
