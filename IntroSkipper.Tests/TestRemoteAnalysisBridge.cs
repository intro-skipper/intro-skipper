// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.Remote;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.SegmentChanges;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using SegmentScanner.Contracts;
using Xunit;
using PluginMode = IntroSkipper.Data.AnalysisMode;
using RemoteMode = SegmentScanner.Contracts.RemoteAnalysisMode;

/// <summary>
/// Focused durable-job and result-admission tests for the remote bridge.
/// </summary>
public sealed class RemoteAnalysisBridgeTests : IDisposable
{
    private const string PluginId = "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b";
    private const string CreditsProfileHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly SegmentChangeHarness _harness = new();

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData("Introduction")]
    [InlineData("Credits")]
    public void RemoteMode_KeepsWireValuesWithoutCollidingWithPluginSchema(string wireValue)
    {
        Assert.NotEqual(typeof(PluginMode).Name, typeof(RemoteMode).Name);
        var mode = Enum.Parse<RemoteMode>(wireValue);
        var json = JsonSerializer.Serialize(mode, WireJson.Options);
        Assert.Equal($"\"{wireValue}\"", json);
        Assert.Equal(mode, JsonSerializer.Deserialize<RemoteMode>(json, WireJson.Options));
        var revisions = JsonSerializer.Deserialize<Dictionary<RemoteMode, string>>($"{{\"{wireValue}\":\"revision\"}}", WireJson.Options);
        Assert.Equal("revision", revisions![mode]);
    }

    [Fact]
    public void IntroductionProfile_UsesEffectiveActionAndChapterPolicy()
    {
        var configuration = new PluginConfiguration
        {
            PreferChromaprint = true,
            ChapterAnalyzerIntroductionPattern = "^Intro$",
            FullLengthChapters = true,
            EnableSponsorBlockChapterDetection = true,
        };

        var chapter = IntroSkipper.Remote.RemoteAnalysisDispatcher.GetIntroductionSettings(configuration, action: AnalyzerAction.Chapter);
        var chromaprint = IntroSkipper.Remote.RemoteAnalysisDispatcher.GetIntroductionSettings(configuration, action: AnalyzerAction.Chromaprint);

        Assert.False(chapter.GetProperty("preferChromaprint").GetBoolean());
        Assert.True(chromaprint.GetProperty("preferChromaprint").GetBoolean());
        Assert.Equal("^Intro$", chapter.GetProperty("chapterAnalyzerIntroductionPattern").GetString());
        Assert.True(chapter.GetProperty("fullLengthChapters").GetBoolean());
        Assert.True(chapter.GetProperty("enableSponsorBlockChapterDetection").GetBoolean());
    }

    [Fact]
    public async Task Lease_RejectsStaleGeneration_AndCanBeClaimedAgain()
    {
        var snapshot = CreateSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0", nodeId: "worker-a");
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);

        var first = await _harness.Database.ClaimRemoteJobAsync("worker-a", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        Assert.False(await _harness.Database.ReleaseRemoteLeaseAsync(first.JobId, first.LeaseToken!, first.LeaseGeneration + 1, default));
        Assert.True(await _harness.Database.ReleaseRemoteLeaseAsync(first.JobId, first.LeaseToken!, first.LeaseGeneration, default));

        var second = await _harness.Database.ClaimRemoteJobAsync("worker-a", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(second);
        Assert.True(second.LeaseGeneration > first.LeaseGeneration);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
    }

    [Fact]
    public async Task Lease_RenewalRequiresCurrentTokenGenerationAndExpiry()
    {
        await _harness.Database.QueueRemoteJobAsync(CreateSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0"), default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        Assert.True(await _harness.Database.RenewRemoteLeaseAsync(lease.JobId, lease.LeaseToken!, lease.LeaseGeneration, TimeSpan.FromMinutes(2), default));
        DateTime renewedExpiry;
        using (var db = _harness.Context())
        {
            renewedExpiry = db.RemoteJobs.Single(job => job.Id == lease.JobId).LeaseExpiresUtc!.Value;
        }

        Assert.True(renewedExpiry > lease.LeaseExpiresUtc!.Value.UtcDateTime);

        Assert.False(await _harness.Database.RenewRemoteLeaseAsync(lease.JobId, "wrong-token", lease.LeaseGeneration, TimeSpan.FromMinutes(2), default));
        using (var db = _harness.Context())
        {
            Assert.Equal(renewedExpiry, db.RemoteJobs.Single(job => job.Id == lease.JobId).LeaseExpiresUtc);
        }

        Assert.False(await _harness.Database.RenewRemoteLeaseAsync(lease.JobId, lease.LeaseToken!, lease.LeaseGeneration + 1, TimeSpan.FromMinutes(2), default));
        using (var db = _harness.Context())
        {
            Assert.Equal(renewedExpiry, db.RemoteJobs.Single(job => job.Id == lease.JobId).LeaseExpiresUtc);
        }

        var expiredExpiry = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _harness.Context())
        {
            db.RemoteJobs.Single(job => job.Id == lease.JobId).LeaseExpiresUtc = expiredExpiry;
            await db.SaveChangesAsync();
        }

        Assert.False(await _harness.Database.RenewRemoteLeaseAsync(lease.JobId, lease.LeaseToken!, lease.LeaseGeneration, TimeSpan.FromMinutes(2), default));
        using (var db = _harness.Context())
        {
            Assert.Equal(expiredExpiry, db.RemoteJobs.Single(job => job.Id == lease.JobId).LeaseExpiresUtc);
        }
    }

    [Fact]
    public async Task Queue_DeduplicatesEquivalentActiveJobs()
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        await _harness.Database.QueueRemoteJobAsync(CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0"), default);
        await _harness.Database.QueueRemoteJobAsync(CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0"), default);

        var claimed = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(claimed);
        Assert.Null(await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task QueueAndClaim_PreserveSignedFileVersionTokenAndRejectNegativeRevisionOrRuntime()
    {
        const string fileVersion = "-123456789";
        var snapshot = CreateSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0", fileVersion);

        Assert.True(await _harness.Database.QueueRemoteJobAsync(snapshot, default));
        using (var db = _harness.Context())
        {
            var stored = Assert.Single(db.RemoteJobs);
            Assert.Equal(WireJson.Write(snapshot), stored.SnapshotJson);
            Assert.Equal(WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(stored.SnapshotJson)), stored.SnapshotHash);
        }

        var claimed = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.Equal(fileVersion, Assert.Single(claimed!.Items).FileVersionToken);

        var item = Assert.Single(snapshot.Items);
        await Assert.ThrowsAsync<InvalidDataException>(() => _harness.Database.QueueRemoteJobAsync(snapshot with
        {
            Items = [item with { ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Introduction] = "-1" } }],
        }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => _harness.Database.QueueRemoteJobAsync(snapshot with
        {
            Items = [item with { RuntimeTicks = -1 }],
        }, default));
    }

    [Fact]
    public async Task Result_RejectsManualEditRace_ReplaysReceipt_AndKeepsJournaledUserState()
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

        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);

        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        var change = await _harness.Database.ApplyChangeAsync(
            new AddUserSegmentIntent(itemId, PluginMode.Introduction, 100, 200));
        Assert.Null(change.Outcome);

        var result = CreateResult(lease, itemId, fileVersion);
        var first = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [result],
            default));
        var replay = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [result],
            default));

        Assert.Equal("Rejected", first.Outcome);
        Assert.Equal("RevisionChanged", first.Reason);
        Assert.Equal(first, replay);
        var user = Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, user.Source);
        Assert.Contains(itemId, await _harness.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task Result_AcceptsButDoesNotReplaceUserSuppressedState()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);

        await _harness.Database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, PluginMode.Introduction, 100, 200));
        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, itemId, fileVersion)],
            default));

        Assert.Equal("Accepted", receipt.Outcome);
        Assert.Equal(SegmentSource.User, Assert.Single(await _harness.Database.GetSegmentsAsync(itemId)).Source);
        Assert.Contains(itemId, await _harness.QueuedItemIdsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_RechecksOnlyTheSelectedChapterSource(bool embedded)
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);
        Plugin.Instance!.Configuration.PreferChromaprint = true;
        Plugin.Instance.Configuration.AdjustIntroBasedOnChapters = false;
        Plugin.Instance.Configuration.UseEmbeddedChaptersForAnalysis = embedded;
        var chapters = new List<ChapterInfo> { new() { Name = "Intro", StartPositionTicks = TimeSpan.FromSeconds(20).Ticks } };
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_chapterRepository",
            ChapterManagerStub.Create(chapters));

        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion) with
        {
            Items =
            [
                CreateSnapshotItem(itemId, seasonId, fileVersion) with
                {
                    Chapters = IntroSkipper.Remote.RemoteAnalysisDispatcher.GetChapterSnapshots(new QueuedEpisode
                    {
                        Chapters = chapters,
                        ChaptersSource = embedded ? ChapterSource.Embedded : ChapterSource.JellyfinTable,
                    }),
                    ServerAnalysisConfigHash = ConfigHasher.Analysis(Plugin.Instance.Configuration, PluginMode.Introduction, AnalyzerAction.Default, ffmpegValid: false),
                },
            ],
        };
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        chapters[0] = new ChapterInfo { Name = "Intro", StartPositionTicks = TimeSpan.FromSeconds(30).Ticks };
        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, itemId, fileVersion)],
            default));

        Assert.Equal(embedded ? "Accepted" : "Rejected", receipt.Outcome);
        Assert.Equal(embedded ? null : "ChapterInputChanged", receipt.Reason);
    }

    [Fact]
    public async Task Result_RejectsDuplicateItemModeBeforeWritingAnyRow()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        snapshot = snapshot with { Items = [.. snapshot.Items, CreateSnapshotItem(Guid.NewGuid(), seasonId, "0")] };
        Assert.True(await _harness.Database.QueueRemoteJobAsync(snapshot, default));
        var lease = Assert.IsType<JobSnapshot>(await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default));
        var first = CreateResult(lease, itemId, fileVersion);
        var second = first with
        {
            AnalysisId = Guid.CreateVersion7(),
            Segments = [first.Segments[0] with { StartTicks = 300, EndTicks = 400 }],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId, new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration), [first, second], default));

        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));
        using var db = _harness.Context();
        Assert.Empty(db.RemoteImportReceipts);
        Assert.Empty(db.RemoteAnalysisMetadata.Where(row => row.CanonicalJson != null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_RechecksAndRecordsSubtitleVersion(bool changed)
    {
        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode.mkv");
        var subtitlePath = directory.Join("episode.eng.srt");
        await File.WriteAllTextAsync(mediaPath, string.Empty);
        await File.WriteAllTextAsync(subtitlePath, "Previously on");
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(itemId, seriesId, seasonId, path: mediaPath);
        var season = JellyfinItems.Season(seasonId, seriesId);
        var series = JellyfinItems.Series(seriesId);
        EntrypointTestHelpers.SetPropertyOrField(season, "ParentId", seriesId);
        EntrypointTestHelpers.SetPropertyOrField(series, "ParentId", libraryId);
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, PluginMode.Recap);
        var config = Plugin.Instance!.Configuration;
        config.EnableSubtitleRecapDetection = true;
        var queued = CreateQueuedEpisode(itemId, seasonId, seriesId, "0");
        queued.Path = mediaPath;
        queued.FileVersion = SeasonResolver.FileVersion(episode);
        var settings = RemoteAnalysisDispatcher.GetScanSettings(config);
        var version = SubtitleSidecarFiles.FileVersion(queued, SubtitleLanguageSelection.Parse(config.SubtitleLanguages), new SubtitleSidecarFiles.FolderListings());
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0") with
        {
            Modes = [RemoteMode.Recap],
            ScanSettings = settings,
            Items =
            [
                CreateSnapshotItem(itemId, seasonId, "0") with
                {
                    SourcePath = mediaPath,
                    SubtitleFileVersionToken = version!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ExpectedModeRevisions = new() { [RemoteMode.Recap] = "0" },
                    ServerAnalysisConfigHash = ConfigHasher.Analysis(config, PluginMode.Recap, AnalyzerAction.Default, ffmpegValid: false),
                },
            ],
        };
        var node = await _harness.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "worker",
            Name = "worker",
            LibraryIds = [libraryId],
            Modes = [RemoteMode.Recap],
            ServerPaths = [new RemoteNodePath { Prefix = directory.Info.FullName }],
        }, default);
        var lease = await QueueLegacyLeasedJobAsync(snapshot with { NodeRevision = node.Revision });
        if (changed) await File.AppendAllTextAsync(subtitlePath, " changed");
        var result = CreateResult(lease, itemId, "0", mode: RemoteMode.Recap) with
        {
            SourcePath = mediaPath,
            Detector = new DetectorIdentity { Id = "test", ProfileHash = WireJson.Hash(settings), ProductionApproved = true },
        };
        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(lease.JobId, new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration), [result], default));
        Assert.Equal(changed ? "SubtitleInputChanged" : null, receipt.Reason);
        Assert.Equal(changed ? "Rejected" : "Accepted", receipt.Outcome);
        var records = (await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId])).AnalysisRecords;
        if (changed) Assert.Empty(records);
        else Assert.Equal(version, records[(itemId, PluginMode.Recap)].FileVersion);
    }

    [Fact]
    public async Task Result_OnlyAuthoritativeNegativeClearsAutomaticState()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);

        await _harness.Database.ReplaceAutoSegmentsAsync(itemId, PluginMode.Introduction, [new Segment(itemId, new IntroSkipper.Data.TimeRange(10, 20))], SegmentSource.Chromaprint);
        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var failedSnapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(failedSnapshot, default);
        var failedLease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(failedLease);

        var failed = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            failedLease.JobId,
            new LeaseRequest(failedLease.LeaseToken!, failedLease.LeaseGeneration),
            [CreateResult(failedLease, itemId, fileVersion, AnalysisStatus.Failed, productionApproved: false)],
            default));
        Assert.Equal("Retained", failed.Outcome);
        Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));

        var negativeSnapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(negativeSnapshot, default);
        var negativeLease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(negativeLease);

        var negative = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            negativeLease.JobId,
            new LeaseRequest(negativeLease.LeaseToken!, negativeLease.LeaseGeneration),
            [CreateResult(negativeLease, itemId, fileVersion, AnalysisStatus.CompletedNoSegments)],
            default));
        Assert.Equal("Accepted", negative.Outcome);
        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));
    }

    [Fact]
    public async Task Result_RejectsIncompleteNegativeCoverage_AndKeepsAutomaticState()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);

        await _harness.Database.ReplaceAutoSegmentsAsync(itemId, PluginMode.Introduction, [new Segment(itemId, new IntroSkipper.Data.TimeRange(10, 20))], SegmentSource.Chromaprint);
        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        var empty = CreateResult(lease, itemId, fileVersion, AnalysisStatus.CompletedNoSegments) with { SearchedRanges = [] };
        await Assert.ThrowsAsync<InvalidDataException>(() => _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [empty],
            default));
        Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));

        var incomplete = CreateResult(lease, itemId, fileVersion, AnalysisStatus.CompletedNoSegments) with
        {
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(0, TimeSpan.FromSeconds(1).Ticks)],
        };
        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [incomplete],
            default));

        Assert.Equal("Rejected", receipt.Outcome);
        Assert.Equal("IncompleteNegativeSearchCoverage", receipt.Reason);
        Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));
    }

    [Fact]
    public async Task CanonicalReadAndReplay_ReflectManualStateAndDisabledRevision()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);

        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var submitted = CreateResult(lease, itemId, fileVersion);
        var accepted = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [submitted],
            default));
        Assert.Equal(1, accepted.Current!.ResultRevision);

        var remote = Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));
        await _harness.Database.ApplyChangeAsync(new DeleteSegmentIntent(itemId, remote.Id));
        var tombstoned = Assert.Single(await _harness.Database.GetRemoteResultsAsync(itemId, default));
        Assert.Equal(2, tombstoned.ResultRevision);
        Assert.Empty(tombstoned.Result.Segments);

        await _harness.Database.ApplyChangeAsync(new ReplaceUserSegmentsForModeIntent(
            itemId,
            PluginMode.Introduction,
            [new SegmentRange(300, 400)]));

        var current = Assert.Single(await _harness.Database.GetRemoteResultsAsync(itemId, default));
        Assert.Equal(3, current.ResultRevision);
        var manual = Assert.Single(current.Result.Segments);
        Assert.Equal(300, manual.StartTicks);
        Assert.Equal(400, manual.EndTicks);
        Assert.Contains("User", manual.Reasons);

        var replay = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [submitted],
            default));
        Assert.Equal(3, replay.Current!.ResultRevision);
        Assert.Equal(300, Assert.Single(replay.Current.Result.Segments).StartTicks);

        await _harness.Database.ApplyChangeAsync(new SegmentVisibilityChangeIntent(itemId, Visible: false));
        var disabled = Assert.Single(await _harness.Database.GetRemoteResultsAsync(itemId, default));
        Assert.Equal(4, disabled.ResultRevision);
        Assert.True(disabled.Disabled);
    }

    [Fact]
    public async Task Result_PersistsServerAnalysisConfigHash_AndRejectsCurrentPolicyChange()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);

        var fileVersion = (IntroSkipper.Manager.SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var configHash = ConfigHasher.Analysis(Plugin.Instance!.Configuration, PluginMode.Introduction, AnalyzerAction.Default, ffmpegValid: false);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion, configHash);
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        var accepted = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, itemId, fileVersion)],
            default));
        Assert.Equal("Accepted", accepted.Outcome);
        var completed = await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId]);
        Assert.Equal(configHash, completed.AnalysisRecords[(itemId, PluginMode.Introduction)].ConfigHash);

        var staleSnapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "1", fileVersion, configHash);
        await _harness.Database.QueueRemoteJobAsync(staleSnapshot, default);
        var staleLease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(staleLease);
        Plugin.Instance.Configuration.PreferChromaprint = true;

        var rejected = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            staleLease.JobId,
            new LeaseRequest(staleLease.LeaseToken!, staleLease.LeaseGeneration),
            [CreateResult(staleLease, itemId, fileVersion)],
            default));
        Assert.Equal("Rejected", rejected.Outcome);
        Assert.Equal("AnalysisConfigChanged", rejected.Reason);
        var retained = await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId]);
        Assert.Equal(configHash, retained.AnalysisRecords[(itemId, PluginMode.Introduction)].ConfigHash);
    }

    [Theory]
    [InlineData(PluginMode.Introduction)]
    [InlineData(PluginMode.Credits)]
    public async Task RemoteOwnership_QueuesCollectionFolderMembersWithForeignPhysicalParent(PluginMode mode)
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(itemId, seriesId, seasonId);
        var season = JellyfinItems.Season(seasonId, seriesId);
        var series = JellyfinItems.Series(seriesId);
        EntrypointTestHelpers.SetPropertyOrField(season, "ParentId", seriesId);
        EntrypointTestHelpers.SetPropertyOrField(series, "ParentId", Guid.NewGuid());
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, mode);
        var task = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            null!,
            new StubFFmpegService(),
            DatabaseTestHelpers.CreateTempCacheService(),
            null!,
            _harness.Database,
            new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create()));
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

        await task.AnalyzeItemsAsync([CreateQueuedEpisode(itemId, seasonId, seriesId, fileVersion)], mode, AnalyzerAction.Default, false, CancellationToken.None);

        Assert.False((await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId])).AnalysisRecords.ContainsKey((itemId, mode)));
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var remoteMode = mode == PluginMode.Introduction ? RemoteMode.Introduction : RemoteMode.Credits;
        Assert.Equal(remoteMode, Assert.Single(lease.Modes));
        var result = mode == PluginMode.Credits
            ? CreateResult(lease, itemId, fileVersion, AnalysisStatus.NeedsReview, productionApproved: false, mode: remoteMode)
            : CreateResult(lease, itemId, fileVersion);
        Assert.Equal(mode == PluginMode.Credits ? "Held" : "Accepted", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [result],
            default)).Outcome);
    }

    [Theory]
    [InlineData(CreditsDetectionMethod.TextAware)]
    [InlineData(CreditsDetectionMethod.IntroSkipper)]
    public async Task RemoteCreditsOwnership_SettlesChapterBeforeQueueingUnmatchedSibling(CreditsDetectionMethod method)
    {
        var chapteredId = Guid.NewGuid();
        var unmatchedId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var chapteredEpisode = JellyfinItems.Episode(chapteredId, seriesId, seasonId);
        var unmatchedEpisode = JellyfinItems.Episode(unmatchedId, seriesId, seasonId);
        var season = JellyfinItems.Season(seasonId, seriesId);
        var series = JellyfinItems.Series(seriesId);
        EntrypointTestHelpers.SetPropertyOrField(season, "ParentId", seriesId);
        EntrypointTestHelpers.SetPropertyOrField(series, "ParentId", libraryId);
        using var scope = ConfigureRemotePlugin(libraryId, chapteredEpisode, season, series, PluginMode.Credits);
        EnsureTestNode("worker", creditsMethod: method);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            CreateRemoteLibraryManager([new Folder { Id = libraryId }], chapteredEpisode, unmatchedEpisode, season, series));
        var ffmpeg = new StubFFmpegService { KeyFrames = (_, _, _) => [] };
        var task = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            null!,
            ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(),
            null!,
            _harness.Database,
            new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create()));
        var chapteredVersion = (SeasonResolver.FileVersion(chapteredEpisode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var unmatchedVersion = (SeasonResolver.FileVersion(unmatchedEpisode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chaptered = CreateQueuedEpisode(chapteredId, seasonId, seriesId, chapteredVersion);
        chaptered.Chapters =
        [
            new ChapterInfo { Name = "Main", StartPositionTicks = 0 },
            new ChapterInfo { Name = "Ending", StartPositionTicks = TimeSpan.FromSeconds(180).Ticks },
            new ChapterInfo { Name = "Epilogue", StartPositionTicks = TimeSpan.FromSeconds(220).Ticks },
        ];
        var unmatched = CreateQueuedEpisode(unmatchedId, seasonId, seriesId, unmatchedVersion);
        unmatched.EpisodeNumber = 2;
        unmatched.Path = "/media/series/s01e02.mkv";

        await task.AnalyzeItemsAsync(
            [chaptered, unmatched],
            PluginMode.Credits,
            AnalyzerAction.Default,
            ffmpegValid: false,
            CancellationToken.None);

        var chapter = Assert.Single(await _harness.Database.GetSegmentsAsync(chapteredId));
        Assert.Equal(SegmentSource.Chapter, chapter.Source);
        Assert.Equal((180, 220), (chapter.ToSegment().Start, chapter.ToSegment().End));
        Assert.Empty(await _harness.Database.GetSegmentsAsync(unmatchedId));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
        Assert.Equal(0, ffmpeg.ProbeCalls);

        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.Equal(RemoteMode.Credits, Assert.Single(lease.Modes));
        Assert.Equal(method, lease.CreditsMethod);
        var remoteItem = Assert.Single(lease.Items, item => item.WriteTarget);
        Assert.Equal(unmatchedId, remoteItem.ItemId);
        Assert.True(remoteItem.WriteTarget);
        if (method == CreditsDetectionMethod.IntroSkipper)
        {
            Assert.False(lease.Items.Single(item => item.ItemId == chapteredId).WriteTarget);
            Assert.NotNull(lease.CreditsSettings);
        }
        else
        {
            Assert.Single(lease.Items);
        }
    }

    [Fact]
    public async Task RemoteOwnership_RequiresSelectedCollectionFolderAtQueueAndImport()
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
        var collectionFolders = new List<Folder> { new() { Id = Guid.NewGuid() } };
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, collectionFolders: collectionFolders);
        var dispatcher = new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create());
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var target = CreateQueuedEpisode(itemId, seasonId, seriesId, fileVersion);
        target.AnalysisConfigHash = ConfigHasher.Analysis(Plugin.Instance!.Configuration, PluginMode.Introduction, AnalyzerAction.Default, ffmpegValid: false);

        Assert.Empty(await dispatcher.QueueAsync([target], [], PluginMode.Introduction, default));

        collectionFolders[0] = new Folder { Id = libraryId };
        Assert.Contains(itemId, await dispatcher.QueueAsync([target], [], PluginMode.Introduction, default));
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        collectionFolders.Clear();
        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, itemId, fileVersion)],
            default));

        Assert.Equal("Rejected", receipt.Outcome);
        Assert.Equal("RemotePolicyChanged", receipt.Reason);
        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));
        Assert.False((await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId])).AnalysisRecords.ContainsKey((itemId, PluginMode.Introduction)));
    }

    [Fact]
    public async Task RemoteOwnership_RetriesFailure_HoldsReview_AndNeverSettlesLocalAnalysis()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);
        var configuration = Plugin.Instance!.Configuration;
        var dispatcher = new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create());
        var task = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            null!,
            new StubFFmpegService(),
            DatabaseTestHelpers.CreateTempCacheService(),
            null!,
            _harness.Database,
            dispatcher);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

        await task.AnalyzeItemsAsync([CreateQueuedEpisode(itemId, seasonId, seriesId, fileVersion)], PluginMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
        Assert.False((await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId])).AnalysisRecords.ContainsKey((itemId, PluginMode.Introduction)));

        var failedLease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(failedLease);
        Assert.Equal("Retained", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            failedLease.JobId,
            new LeaseRequest(failedLease.LeaseToken!, failedLease.LeaseGeneration),
            [CreateResult(failedLease, itemId, fileVersion, AnalysisStatus.Failed)],
            default)).Outcome);

        var retry = CreateQueuedEpisode(itemId, seasonId, seriesId, fileVersion);
        var afterFailure = await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId]);
        new QueueVerifier(configuration, [PluginMode.Introduction], afterFailure, false).Classify(retry);
        Assert.Equal(EpisodeState.NotAnalyzed, retry.GetAnalyzed(PluginMode.Introduction));
        await task.AnalyzeItemsAsync([retry], PluginMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);

        var reviewLease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(reviewLease);
        Assert.Equal("Held", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            reviewLease.JobId,
            new LeaseRequest(reviewLease.LeaseToken!, reviewLease.LeaseGeneration),
            [CreateResult(reviewLease, itemId, fileVersion, AnalysisStatus.NeedsReview, productionApproved: false)],
            default)).Outcome);

        var held = CreateQueuedEpisode(itemId, seasonId, seriesId, fileVersion);
        var afterReview = await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId]);
        Assert.False(afterReview.AnalysisRecords.ContainsKey((itemId, PluginMode.Introduction)));
        new QueueVerifier(configuration, [PluginMode.Introduction], afterReview, false).Classify(held);
        Assert.Equal(EpisodeState.NotAnalyzed, held.GetAnalyzed(PluginMode.Introduction));
        await task.AnalyzeItemsAsync([held], PluginMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
        Assert.Null(await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default));

        configuration.PreferChromaprint = true;
        await task.AnalyzeItemsAsync(
            [CreateQueuedEpisode(itemId, seasonId, seriesId, fileVersion)],
            PluginMode.Introduction,
            AnalyzerAction.Default,
            false,
            CancellationToken.None);
        Assert.NotNull(await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task NeedsReview_HoldsExperimentalCreditsWithoutApprovedProfile()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, PluginMode.Credits);
        var configuration = Plugin.Instance!.Configuration;
        Assert.Empty(configuration.ApprovedRemoteCreditsProfileHashes);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var configHash = ConfigHasher.Analysis(configuration, PluginMode.Credits, AnalyzerAction.Default, ffmpegValid: false);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion, configHash) with
        {
            Modes = [RemoteMode.Credits],
            CreditsProfileHash = configuration.RemoteCreditsProfileHash,
            Items =
            [
                CreateSnapshotItem(itemId, seasonId, fileVersion, serverAnalysisConfigHash: configHash) with
                {
                    ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Credits] = "0" },
                },
            ],
        };
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var result = CreateResult(lease, itemId, fileVersion, AnalysisStatus.NeedsReview, productionApproved: false, mode: RemoteMode.Credits) with
        {
            Detector = new DetectorIdentity
            {
                Id = "experimental-credits",
                ProfileHash = configuration.RemoteCreditsProfileHash,
                ProductionApproved = false,
            },
        };

        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [result],
            default));

        Assert.Equal("Held", receipt.Outcome);
        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));
        Assert.False((await _harness.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId])).AnalysisRecords.ContainsKey((itemId, PluginMode.Credits)));
    }

    [Fact]
    public async Task AutomaticImport_PromotesOnlyOptedInCreditsAndReplaysTheRawReceipt()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, PluginMode.Credits);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var defaultLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "default-worker");
        var held = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            defaultLease.JobId,
            new LeaseRequest(defaultLease.LeaseToken!, defaultLease.LeaseGeneration),
            [CreateAutomaticCreditsResult(defaultLease, itemId, fileVersion)],
            default));
        Assert.Equal("Held", held.Outcome);

        var automaticLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "automatic-worker", automaticImports: true);
        var raw = CreateAutomaticCreditsResult(automaticLease, itemId, fileVersion);
        var accepted = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            automaticLease.JobId,
            new LeaseRequest(automaticLease.LeaseToken!, automaticLease.LeaseGeneration),
            [raw],
            default));

        Assert.Equal("Accepted", accepted.Outcome);
        Assert.NotNull(accepted.Current);
        var canonical = accepted.Current.Result;
        Assert.Equal(AnalysisStatus.CompletedWithSegments, canonical.Status);
        Assert.False(canonical.Detector.ProductionApproved);
        Assert.Contains("AutomaticImportByNodePolicy", canonical.Warnings);
        var segment = Assert.Single(canonical.Segments);
        Assert.Equal(CandidateDecision.Accept, segment.Decision);
        Assert.Equal("ProfileOptIn", segment.ConfidenceTier);
        using (var db = _harness.Context())
        {
            Assert.Equal(WireJson.Hash(raw), Assert.Single(db.RemoteImportReceipts.Where(receipt => receipt.AnalysisId == raw.AnalysisId)).PayloadHash);
        }

        var replay = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            automaticLease.JobId,
            new LeaseRequest(automaticLease.LeaseToken!, automaticLease.LeaseGeneration),
            [raw],
            default));
        Assert.Equal("Accepted", replay.Outcome);
        Assert.Equal(accepted.Current.ResultRevision, replay.Current!.ResultRevision);
    }

    [Fact]
    public async Task AutomaticImport_HoldsIncompleteUnknownAndInsufficientNegativeCredits()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, PluginMode.Credits);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var incompleteLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "automatic-worker", automaticImports: true);
        var incomplete = CreateAutomaticCreditsResult(incompleteLease, itemId, fileVersion) with { Warnings = ["ExperimentalCreditsGroupPolicy", "DecodeIncomplete"] };
        Assert.Equal("Held", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            incompleteLease.JobId,
            new LeaseRequest(incompleteLease.LeaseToken!, incompleteLease.LeaseGeneration),
            [incomplete],
            default)).Outcome);

        var unknownLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "automatic-worker", automaticImports: true);
        var unknown = CreateAutomaticCreditsResult(unknownLease, itemId, fileVersion);
        unknown = unknown with
        {
            Segments = [unknown.Segments.Single() with { Reasons = ["OrderedCreditsGroupPolicy", "ProfileNotCalibrated", "UnrecognizedReason"] }],
        };
        Assert.Equal("Held", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            unknownLease.JobId,
            new LeaseRequest(unknownLease.LeaseToken!, unknownLease.LeaseGeneration),
            [unknown],
            default)).Outcome);

        await _harness.Database.ReplaceAutoSegmentsAsync(itemId, PluginMode.Credits, [new Segment(itemId, new IntroSkipper.Data.TimeRange(10, 20))], SegmentSource.Chromaprint);
        var incompleteEmptyLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "automatic-worker", automaticImports: true);
        var incompleteEmpty = CreateAutomaticCreditsResult(incompleteEmptyLease, itemId, fileVersion) with
        {
            Segments = [],
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(TimeSpan.FromMinutes(4).Ticks - 1, TimeSpan.FromMinutes(4).Ticks)],
        };
        Assert.Equal("Held", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            incompleteEmptyLease.JobId,
            new LeaseRequest(incompleteEmptyLease.LeaseToken!, incompleteEmptyLease.LeaseGeneration),
            [incompleteEmpty],
            default)).Outcome);
        Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));

        var negativeLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "automatic-worker", automaticImports: true);
        var insufficient = CreateAutomaticCreditsResult(negativeLease, itemId, fileVersion) with
        {
            Status = AnalysisStatus.CompletedNoSegments,
            Segments = [],
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(TimeSpan.FromMinutes(4).Ticks - 1, TimeSpan.FromMinutes(4).Ticks)],
        };
        var rejected = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            negativeLease.JobId,
            new LeaseRequest(negativeLease.LeaseToken!, negativeLease.LeaseGeneration),
            [insufficient],
            default));
        Assert.Equal("Rejected", rejected.Outcome);
        Assert.Equal("IncompleteNegativeSearchCoverage", rejected.Reason);
        Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));

        var completeEmptyLease = await QueueCreditsLeaseAsync(itemId, seasonId, fileVersion, "automatic-worker", automaticImports: true);
        var completeEmpty = CreateAutomaticCreditsResult(completeEmptyLease, itemId, fileVersion) with
        {
            Segments = [],
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(TimeSpan.FromMinutes(4).Ticks - (TimeSpan.FromMinutes(4).Ticks / 5), TimeSpan.FromMinutes(4).Ticks)],
        };
        var cleared = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            completeEmptyLease.JobId,
            new LeaseRequest(completeEmptyLease.LeaseToken!, completeEmptyLease.LeaseGeneration),
            [completeEmpty],
            default));
        Assert.Equal("Accepted", cleared.Outcome);
        Assert.NotNull(cleared.Current);
        Assert.Equal(AnalysisStatus.CompletedNoSegments, cleared.Current.Result.Status);
        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));
        using (var db = _harness.Context())
        {
            Assert.Equal(WireJson.Hash(completeEmpty), Assert.Single(db.RemoteImportReceipts.Where(receipt => receipt.AnalysisId == completeEmpty.AnalysisId)).PayloadHash);
        }
    }

    [Fact]
    public async Task HeldCreditsReview_RequiresCurrentPin_AndPromotesExactlyOnce()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, PluginMode.Credits);
        var configuration = Plugin.Instance!.Configuration;
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var configHash = ConfigHasher.Analysis(configuration, PluginMode.Credits, AnalyzerAction.Default, ffmpegValid: false);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion, configHash) with
        {
            Modes = [RemoteMode.Credits],
            CreditsProfileHash = configuration.RemoteCreditsProfileHash,
            Items =
            [
                CreateSnapshotItem(itemId, seasonId, fileVersion, serverAnalysisConfigHash: configHash) with
                {
                    ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Credits] = "0" },
                },
            ],
        };
        var lease = await QueueLegacyLeasedJobAsync(snapshot with { NodeId = null, NodeRevision = null });
        var held = CreateResult(lease, itemId, fileVersion, AnalysisStatus.NeedsReview, productionApproved: false, mode: RemoteMode.Credits) with
        {
            Detector = new DetectorIdentity { Id = "experimental-credits", ProfileHash = CreditsProfileHash, ProductionApproved = false },
            Segments =
            [
                new SegmentCandidate
                {
                    CandidateId = "candidate",
                    Type = RemoteMode.Credits,
                    StartTicks = 100,
                    EndTicks = 200,
                    Decision = CandidateDecision.Review,
                },
            ],
        };
        await using (var db = _harness.Context())
        {
            db.RemoteImportReceipts.Add(new DbRemoteImportReceipt
            {
                JobId = lease.JobId,
                AnalysisId = held.AnalysisId,
                ItemId = itemId,
                Mode = (int)RemoteMode.Credits,
                PayloadHash = WireJson.Hash(held),
                Outcome = "Held",
                CreatedUtc = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var review = new RemoteReviewRequest(
            [held],
            new ReviewDocument
            {
                Decisions = [new ReviewDecision { AnalysisId = held.AnalysisId, CandidateId = "candidate", Decision = CandidateDecision.Accept }],
            });
        configuration.RemoteCreditsProfileHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.Equal("Rejected", Assert.Single(await _harness.Database.ReviewRemoteResultsAsync(lease.JobId, review, default)).Outcome);
        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));

        configuration.RemoteCreditsProfileHash = CreditsProfileHash;
        var accepted = Assert.Single(await _harness.Database.ReviewRemoteResultsAsync(lease.JobId, review, default));
        Assert.Equal("Accepted", accepted.Outcome);
        Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));
        Assert.Equal(1, accepted.Current!.ResultRevision);
        var replay = Assert.Single(await _harness.Database.ReviewRemoteResultsAsync(lease.JobId, review, default));
        Assert.Equal("Accepted", replay.Outcome);
        Assert.Equal(1, replay.Current!.ResultRevision);
        var alteredReview = review with
        {
            Review = new ReviewDocument
            {
                Decisions = [new ReviewDecision { AnalysisId = held.AnalysisId, CandidateId = "candidate", Decision = CandidateDecision.Reject }],
            },
        };
        Assert.Equal("Conflict", Assert.Single(await _harness.Database.ReviewRemoteResultsAsync(lease.JobId, alteredReview, default)).Outcome);
        Assert.Equal("Conflict", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [held],
            default)).Outcome);
    }

    [Fact]
    public async Task Queue_DoesNotDeduplicateCreditsJobsWithDifferentPins()
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var first = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", nodeId: "worker-a") with
        {
            Modes = [RemoteMode.Credits],
            CreditsProfileHash = CreditsProfileHash,
            Items = [CreateSnapshotItem(itemId, seasonId, "0") with { ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Credits] = "0" } }],
        };
        var second = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", nodeId: "worker-b") with
        {
            Modes = [RemoteMode.Credits],
            CreditsProfileHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Items = [CreateSnapshotItem(itemId, seasonId, "0") with { ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Credits] = "0" } }],
        };
        await _harness.Database.QueueRemoteJobAsync(first, default);
        await _harness.Database.QueueRemoteJobAsync(second, default);
        var firstLease = await _harness.Database.ClaimRemoteJobAsync("worker-a", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(firstLease);
        var secondLease = await _harness.Database.ClaimRemoteJobAsync("worker-b", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(secondLease);
        Assert.NotEqual(firstLease.CreditsProfileHash, secondLease.CreditsProfileHash);
    }

    [Fact]
    public async Task HeldReview_RejectsCurrentRevisionChange()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var held = CreateResult(lease, itemId, fileVersion, AnalysisStatus.NeedsReview, productionApproved: false) with
        {
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
        Assert.Equal("Held", Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [held],
            default)).Outcome);
        await _harness.Database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, PluginMode.Introduction, 300, 400));

        var review = new RemoteReviewRequest(
            [held],
            new ReviewDocument
            {
                Decisions = [new ReviewDecision { AnalysisId = held.AnalysisId, CandidateId = "candidate", Decision = CandidateDecision.Accept }],
            });
        var receipt = Assert.Single(await _harness.Database.ReviewRemoteResultsAsync(lease.JobId, review, default));

        Assert.Equal("Rejected", receipt.Outcome);
        var segment = Assert.Single(await _harness.Database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, segment.Source);
    }

    [Fact]
    public async Task ReviewHold_InvalidatesWhenComparisonInputChanges()
    {
        var targetId = Guid.NewGuid();
        var comparisonId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var targetEpisode = JellyfinItems.Episode(targetId, seriesId, seasonId);
        var comparisonEpisode = JellyfinItems.Episode(comparisonId, seriesId, seasonId);
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
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            CreateRemoteLibraryManager([new Folder { Id = libraryId }], targetEpisode, comparisonEpisode, season, series));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.Create(Array.Empty<ChapterInfo>()));
        EnsureTestNode("worker");
        var targetFileVersion = (SeasonResolver.FileVersion(targetEpisode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var target = CreateQueuedEpisode(targetId, seasonId, seriesId, targetFileVersion);
        var comparison = CreateQueuedEpisode(comparisonId, seasonId, seriesId, "1");
        comparison.EpisodeNumber = 2;
        comparison.SetAnalyzed(PluginMode.Introduction, EpisodeState.UserProvided);
        var dispatcher = new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create());
        var task = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            null!,
            new StubFFmpegService(),
            DatabaseTestHelpers.CreateTempCacheService(),
            null!,
            _harness.Database,
            dispatcher);

        await task.AnalyzeItemsAsync([target, comparison], PluginMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
        var heldLease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(heldLease);
        Assert.False(heldLease.Items.Single(item => item.ItemId == comparisonId).WriteTarget);
        var reviewResult = CreateResult(heldLease, targetId, targetFileVersion, AnalysisStatus.NeedsReview, productionApproved: false);
        var heldReceipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            heldLease.JobId,
            new LeaseRequest(heldLease.LeaseToken!, heldLease.LeaseGeneration),
            [reviewResult],
            default));
        Assert.Equal("Held", heldReceipt.Outcome);

        var changedComparison = CreateQueuedEpisode(comparisonId, seasonId, seriesId, "2");
        changedComparison.EpisodeNumber = comparison.EpisodeNumber;
        changedComparison.SetAnalyzed(PluginMode.Introduction, EpisodeState.UserProvided);
        await task.AnalyzeItemsAsync(
            [CreateQueuedEpisode(targetId, seasonId, seriesId, targetFileVersion), changedComparison],
            PluginMode.Introduction,
            AnalyzerAction.Default,
            false,
            CancellationToken.None);

        Assert.NotNull(await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Job_StaysLeasedUntilEachExpectedItemModePairSubmits()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var first = JellyfinItems.Episode(firstId, seriesId, seasonId);
        var second = JellyfinItems.Episode(secondId, seriesId, seasonId);
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
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", CreateRemoteLibraryManager([new Folder { Id = libraryId }], first, second, season, series));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.Create(Array.Empty<ChapterInfo>()));
        var fileVersion = (SeasonResolver.FileVersion(first) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(firstId, seasonId, Guid.NewGuid(), "0", fileVersion) with
        {
            Items =
            [
                CreateSnapshotItem(firstId, seasonId, fileVersion),
                CreateSnapshotItem(secondId, seasonId, fileVersion),
            ],
        };
        await _harness.Database.QueueRemoteJobAsync(snapshot, default);
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        var firstReceipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, firstId, fileVersion)],
            default));
        Assert.Equal("Accepted", firstReceipt.Outcome);

        var duplicateReceipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, firstId, fileVersion)],
            default));
        Assert.Equal("Rejected", duplicateReceipt.Outcome);
        Assert.Equal("RevisionChanged", duplicateReceipt.Reason);

        var secondReceipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, secondId, fileVersion)],
            default));
        Assert.Equal("Accepted", secondReceipt.Outcome);
    }

    [Fact]
    public async Task NodeRegistry_StartsDisabled_PersistsAndRejectsWrongNodeClaims()
    {
        var registration = new RemoteNodeRegistration
        {
            NodeId = "registered-node",
            Name = "registered-node",
        };

        var registered = await _harness.Database.RegisterRemoteNodeAsync(registration, default);
        Assert.False(registered.Enabled);
        Assert.Equal(1, registered.Revision);
        using (var db = _harness.Context())
        {
            Assert.Equal("registered-node", Assert.Single(db.RemoteNodes).NodeId);
        }

        var enabled = Assert.IsType<RemoteNodeInfo>(await _harness.Database.SetRemoteNodeEnabledAsync(registered.NodeId, true, default));
        var snapshot = CreateSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0", nodeId: enabled.NodeId);
        Assert.Equal(enabled.Revision, snapshot.NodeRevision);
        Assert.True(await _harness.Database.QueueRemoteJobAsync(snapshot, default));

        EnsureTestNode("wrong-node");
        Assert.Null(await _harness.Database.ClaimRemoteJobAsync("wrong-node", TimeSpan.FromMinutes(1), default));
        Assert.NotNull(await _harness.Database.ClaimRemoteJobAsync(enabled.NodeId, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task DisabledNode_RejectsStaleLeaseRenewalAndImport()
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion);
        Assert.True(await _harness.Database.QueueRemoteJobAsync(snapshot, default));
        var lease = await _harness.Database.ClaimRemoteJobAsync("worker", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);

        var disabled = Assert.IsType<RemoteNodeInfo>(await _harness.Database.SetRemoteNodeEnabledAsync(lease.NodeId!, false, default));
        Assert.True(disabled.Revision > lease.NodeRevision);
        Assert.False(await _harness.Database.RenewRemoteLeaseAsync(lease.JobId, lease.LeaseToken!, lease.LeaseGeneration, TimeSpan.FromMinutes(1), default));

        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, itemId, fileVersion)],
            default));
        Assert.Equal("Rejected", receipt.Outcome);
        Assert.Equal("RemotePolicyChanged", receipt.Reason);
    }

    [Fact]
    public async Task NodeRouting_RequiresLibraryAndPathScopeForMovies()
    {
        var libraryId = Guid.NewGuid();
        var otherLibraryId = Guid.NewGuid();
        var ownedMovie = JellyfinItems.Movie(Guid.NewGuid(), path: "/media/owned/feature.mkv");
        var wrongLibraryMovie = JellyfinItems.Movie(Guid.NewGuid(), path: "/media/owned/other.mkv");
        var wrongPathMovie = JellyfinItems.Movie(Guid.NewGuid(), path: "/media/elsewhere/other.mkv");
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.FakeLibraryManager.Create(
                [],
                _ => [],
                id => new[] { ownedMovie, wrongLibraryMovie, wrongPathMovie }.SingleOrDefault(item => item.Id == id),
                getCollectionFolders: item => item.Id == wrongLibraryMovie.Id ? [new Folder { Id = otherLibraryId }] : [new Folder { Id = libraryId }]));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.Create(Array.Empty<ChapterInfo>()));

        var registered = await _harness.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "movie-node",
            Name = "Movie node",
            LibraryIds = [libraryId],
            Modes = [RemoteMode.Introduction],
            ServerPaths = [new RemoteNodePath { Prefix = "/media/owned" }],
        }, default);
        await _harness.Database.SetRemoteNodeEnabledAsync(registered.NodeId, true, default);

        var dispatcher = new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create());
        var routed = await dispatcher.QueueAsync(
        [
            CreateMovieQueueItem(ownedMovie),
            CreateMovieQueueItem(wrongLibraryMovie),
            CreateMovieQueueItem(wrongPathMovie),
        ],
        [],
        PluginMode.Introduction,
        default);

        Assert.Equal([ownedMovie.Id], routed);
        var lease = await _harness.Database.ClaimRemoteJobAsync("movie-node", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.Equal(ownedMovie.Id, Assert.Single(lease.Items).ItemId);
    }

    [Theory]
    [InlineData(PluginMode.Recap, true)]
    [InlineData(PluginMode.Recap, false)]
    [InlineData(PluginMode.Preview, true)]
    [InlineData(PluginMode.Preview, false)]
    public async Task RemotePrerequisite_DefersOnlyWhenPrerequisiteIsRemotelyOwned(PluginMode mode, bool remoteOwnsPrerequisite)
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
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            ScanIntroduction = false,
            ScanCredits = false,
        });
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", CreateRemoteLibraryManager([new Folder { Id = libraryId }], episode, season, series));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.Create(Array.Empty<ChapterInfo>()));

        var targetMode = mode switch
        {
            PluginMode.Recap => RemoteMode.Recap,
            PluginMode.Preview => RemoteMode.Preview,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var prerequisiteMode = mode switch
        {
            PluginMode.Recap => RemoteMode.Introduction,
            PluginMode.Preview => RemoteMode.Credits,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var registered = await _harness.Database.RegisterRemoteNodeAsync(new RemoteNodeRegistration
        {
            NodeId = "prerequisite-node",
            Name = "Prerequisite node",
            LibraryIds = [libraryId],
            Modes = remoteOwnsPrerequisite ? [targetMode, prerequisiteMode] : [targetMode],
            ServerPaths = [new RemoteNodePath { Prefix = "/media" }],
            CreditsProfileHash = CreditsProfileHash,
        }, default);
        await _harness.Database.SetRemoteNodeEnabledAsync(registered.NodeId, true, default);

        var dispatcher = new RemoteAnalysisDispatcher(_harness.Database, RemoteHostProxy.Create());
        var target = CreateQueuedEpisode(itemId, seasonId, seriesId, "0");
        var remotelyOwned = await dispatcher.QueueAsync([target], [], mode, default);

        Assert.Contains(itemId, remotelyOwned);
        var lease = await _harness.Database.ClaimRemoteJobAsync(registered.NodeId, TimeSpan.FromMinutes(1), default);
        if (remoteOwnsPrerequisite)
        {
            Assert.Null(lease);
        }
        else
        {
            Assert.NotNull(lease);
            Assert.Equal(targetMode, Assert.Single(lease.Modes));
        }
    }

    [Theory]
    [InlineData(false, false, false, "Held")]
    [InlineData(true, false, false, "Accepted")]
    [InlineData(true, true, false, "Accepted")]
    [InlineData(true, true, true, "Held")]
    public async Task OriginalCredits_AutomaticImportRequiresOptInAndCompleteCoverage(bool automaticImports, bool empty, bool shortCoverage, string outcome)
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
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series, PluginMode.Credits);
        var version = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lease = await QueueCreditsLeaseAsync(itemId, seasonId, version, "original-node", automaticImports, CreditsDetectionMethod.IntroSkipper);
        var original = CreateAutomaticCreditsResult(lease, itemId, version);
        original = original with
        {
            Detector = original.Detector with { Id = "introskipper-credits-cpu-v1", ModelSha256 = null },
            Warnings = ["OriginalIntroSkipperCredits"],
            Segments = empty ? [] : original.Segments.Select(segment => segment with { Reasons = ["OriginalIntroSkipperCredits", "UpstreamAdjusted"] }).ToList(),
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(0, TimeSpan.FromMinutes(4).Ticks - (shortCoverage ? TimeSpan.TicksPerSecond : 0))],
        };
        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [original],
            default));
        Assert.Equal(outcome, receipt.Outcome);
        if (outcome == "Accepted")
        {
            Assert.Equal(empty ? AnalysisStatus.CompletedNoSegments : AnalysisStatus.CompletedWithSegments, receipt.Current!.Result.Status);
        }
    }

    [Fact]
    public async Task CreditsMethodChange_UpdatesNodeRevisionWhileUnchangedRegistrationDoesNot()
    {
        var registration = new RemoteNodeRegistration { NodeId = "method-node", Name = "Method node" };
        var first = await _harness.Database.RegisterRemoteNodeAsync(registration, default);
        var same = await _harness.Database.RegisterRemoteNodeAsync(registration, default);
        Assert.Equal(first.Revision, same.Revision);
        var original = await _harness.Database.RegisterRemoteNodeAsync(registration with { CreditsMethod = CreditsDetectionMethod.IntroSkipper }, default);
        Assert.True(original.Revision > same.Revision);
        Assert.Equal(CreditsDetectionMethod.IntroSkipper, original.CreditsMethod);
    }

    private JobSnapshot CreateSnapshot(
        Guid itemId,
        Guid seasonId,
        Guid jobId,
        string revision,
        string fileVersion = "0",
        string? serverAnalysisConfigHash = null,
        string nodeId = "worker",
        bool automaticImports = false,
        CreditsDetectionMethod creditsMethod = CreditsDetectionMethod.TextAware)
    {
        var node = EnsureTestNode(nodeId, automaticImports, creditsMethod);
        return new JobSnapshot
        {
            JobId = jobId,
            AttemptId = Guid.CreateVersion7(),
            ServerId = PluginId,
            NodeId = node.NodeId,
            NodeRevision = node.Revision,
            Modes = [RemoteMode.Introduction],
            IntroductionSettings = JsonSerializer.SerializeToElement(new { analysisPercent = 100, analysisLengthLimit = 10 }),
            IntroductionProfileHash = IntroSkipper.Remote.RemoteAnalysisDispatcher.GetIntroductionProfileHash(Plugin.Instance?.Configuration ?? new PluginConfiguration()),
            Items =
            [
                CreateSnapshotItem(itemId, seasonId, fileVersion, revision, serverAnalysisConfigHash),
            ],
        };
    }

    private RemoteNodeInfo EnsureTestNode(string nodeId, bool automaticImports = false, CreditsDetectionMethod creditsMethod = CreditsDetectionMethod.TextAware)
    {
        var configuration = Plugin.Instance?.Configuration;
        var libraryIds = configuration?.RemoteAnalysisLibraryIds.ToList() ?? [];
        var modes = configuration?.RemoteAnalysisModes
            .Select(RemoteModes.ToRemote)
            .ToList()
            ?? [];
        var registration = new RemoteNodeRegistration
        {
            NodeId = nodeId,
            Name = nodeId,
            LibraryIds = libraryIds,
            Modes = modes,
            ServerPaths = libraryIds.Count > 0 && modes.Count > 0 ? [new RemoteNodePath { Prefix = "/media" }] : [],
            CreditsProfileHash = modes.Contains(RemoteMode.Credits) ? configuration?.RemoteCreditsProfileHash ?? CreditsProfileHash : string.Empty,
            AutomaticImports = automaticImports,
            CreditsMethod = creditsMethod,
        };
        var node = _harness.Database.RegisterRemoteNodeAsync(registration, default).GetAwaiter().GetResult();
        return node.Enabled
            ? node
            : Assert.IsType<RemoteNodeInfo>(_harness.Database.SetRemoteNodeEnabledAsync(node.NodeId, true, default).GetAwaiter().GetResult());
    }

    private async Task<JobSnapshot> QueueCreditsLeaseAsync(Guid itemId, Guid seasonId, string fileVersion, string nodeId, bool automaticImports = false, CreditsDetectionMethod method = CreditsDetectionMethod.TextAware)
    {
        var configuration = Plugin.Instance!.Configuration;
        var configHash = ConfigHasher.Analysis(configuration, PluginMode.Credits, AnalyzerAction.Default, ffmpegValid: false);
        var snapshot = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion, configHash, nodeId, automaticImports, method) with
        {
            Modes = [RemoteMode.Credits],
            CreditsProfileHash = configuration.RemoteCreditsProfileHash,
            CreditsMethod = method,
            CreditsSettings = method == CreditsDetectionMethod.IntroSkipper ? RemoteAnalysisDispatcher.GetCreditsSettings(configuration) : null,
            Items = [CreateSnapshotItem(itemId, seasonId, fileVersion, serverAnalysisConfigHash: configHash) with
            {
                ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Credits] = "0" },
            }],
        };
        Assert.True(await _harness.Database.QueueRemoteJobAsync(snapshot, default));
        var lease = await _harness.Database.ClaimRemoteJobAsync(nodeId, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        return lease;
    }

    private static AnalysisResult CreateAutomaticCreditsResult(JobSnapshot lease, Guid itemId, string fileVersion)
        => CreateResult(lease, itemId, fileVersion, AnalysisStatus.NeedsReview, productionApproved: false, mode: RemoteMode.Credits) with
        {
            Detector = new DetectorIdentity
            {
                Id = "plex-observed-experimental",
                ProfileHash = lease.CreditsProfileHash,
                ModelSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                InferenceEnvironment = "test",
                ProductionApproved = false,
            },
            Segments =
            [
                new SegmentCandidate
                {
                    CandidateId = "candidate",
                    Type = RemoteMode.Credits,
                    StartTicks = 100,
                    EndTicks = 200,
                    RawStartTicks = 100,
                    RawEndTicks = 200,
                    Decision = CandidateDecision.Review,
                    Reasons = ["OrderedCreditsGroupPolicy", "ProfileNotCalibrated"],
                },
            ],
            SearchedRanges = [new SegmentScanner.Contracts.TimeRange(0, TimeSpan.FromMinutes(4).Ticks)],
            Warnings = ["ExperimentalCreditsGroupPolicy"],
        };

    [Fact]
    public async Task LegacyLeasedJob_CannotPublishAfterNodeUpgrade()
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(itemId, seasonId, seriesId);
        var season = JellyfinItems.Season(seasonId, seriesId);
        var series = JellyfinItems.Series(seriesId);
        using var scope = ConfigureRemotePlugin(libraryId, episode, season, series);
        var fileVersion = (SeasonResolver.FileVersion(episode) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var legacy = CreateSnapshot(itemId, seasonId, Guid.NewGuid(), "0", fileVersion) with { NodeId = null, NodeRevision = null };
        var lease = await QueueLegacyLeasedJobAsync(legacy);
        var receipt = Assert.Single(await _harness.Database.SubmitRemoteResultsAsync(
            lease.JobId,
            new LeaseRequest(lease.LeaseToken!, lease.LeaseGeneration),
            [CreateResult(lease, itemId, fileVersion)],
            default));
        Assert.Equal("Rejected", receipt.Outcome);
        Assert.Equal("NodeRegistrationRequired", receipt.Reason);
        Assert.Empty(await _harness.Database.GetSegmentsAsync(itemId));
    }

    private async Task<JobSnapshot> QueueLegacyLeasedJobAsync(JobSnapshot snapshot)
    {
        var lease = snapshot with
        {
            AttemptId = Guid.CreateVersion7(),
            LeaseToken = "legacy-lease-token",
            LeaseGeneration = 1,
            LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        var json = WireJson.Write(lease);
        await using var db = _harness.Context();
        db.RemoteJobs.Add(new DbRemoteJob
        {
            Id = lease.JobId,
            AttemptId = lease.AttemptId,
            SnapshotJson = json,
            SnapshotHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(json)),
            State = "Leased",
            WorkerId = "legacy-worker",
            LeaseTokenHash = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(lease.LeaseToken)),
            LeaseGeneration = lease.LeaseGeneration,
            LeaseExpiresUtc = lease.LeaseExpiresUtc.Value.UtcDateTime,
            Attempts = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        foreach (var item in lease.Items)
        {
            foreach (var mode in item.ExpectedModeRevisions.Keys)
            {
                db.RemoteAnalysisMetadata.Add(new DbRemoteAnalysisMetadata { ItemId = item.ItemId, Mode = (int)mode });
            }
        }

        await db.SaveChangesAsync();
        return lease;
    }

    private static JobItem CreateSnapshotItem(Guid itemId, Guid seasonId, string fileVersion, string revision = "0", string? serverAnalysisConfigHash = null)
        => new()
        {
            ItemId = itemId,
            MediaSourceKey = "/media/series/s01e01.mkv",
            SourcePath = "/media/series/s01e01.mkv",
            FileVersionToken = fileVersion,
            ServerAnalysisConfigHash = serverAnalysisConfigHash ?? ConfigHasher.Analysis(new PluginConfiguration(), PluginMode.Introduction, AnalyzerAction.Default, ffmpegValid: false),
            ExpectedModeRevisions = new Dictionary<RemoteMode, string> { [RemoteMode.Introduction] = revision },
            SeasonKey = seasonId.ToString("D"),
            RuntimeTicks = TimeSpan.FromMinutes(4).Ticks,
            ChaptersAreAuthoritative = true,
        };

    private static QueuedEpisode CreateQueuedEpisode(Guid itemId, Guid seasonId, Guid seriesId, string fileVersion)
        => new()
        {
            EpisodeId = itemId,
            SeasonId = seasonId,
            SeriesId = seriesId,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Path = "/media/series/s01e01.mkv",
            Duration = TimeSpan.FromMinutes(4).TotalSeconds,
            FileVersion = long.Parse(fileVersion, System.Globalization.CultureInfo.InvariantCulture),
        };

    private static QueuedEpisode CreateMovieQueueItem(BaseItem movie)
        => new()
        {
            EpisodeId = movie.Id,
            SeasonId = movie.Id,
            Path = movie.Path,
            Duration = TimeSpan.FromMinutes(4).TotalSeconds,
            Category = QueuedMediaCategory.Movie,
            AnalysisConfigHash = ConfigHasher.Analysis(new PluginConfiguration(), PluginMode.Introduction, AnalyzerAction.Default, ffmpegValid: false),
        };

    private IDisposable ConfigureRemotePlugin(
        Guid libraryId,
        BaseItem episode,
        BaseItem season,
        BaseItem series,
        PluginMode mode = PluginMode.Introduction,
        List<Folder>? collectionFolders = null)
    {
        var config = new PluginConfiguration
        {
            RemoteAnalysisEnabled = true,
            RemoteAnalysisModes = [mode],
            RemoteAnalysisLibraryIds = [libraryId],
            RemoteCreditsProfileHash = mode == PluginMode.Credits ? CreditsProfileHash : string.Empty,
        };
        var scope = EntrypointTestHelpers.CreatePluginScope(config);
        var folders = collectionFolders ?? [new Folder { Id = libraryId }];
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", CreateRemoteLibraryManager(folders, episode, season, series));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.Create(Array.Empty<ChapterInfo>()));
        EnsureTestNode("worker");
        return scope;
    }

    private static ILibraryManager CreateRemoteLibraryManager(List<Folder> collectionFolders, params BaseItem[] items)
        => EntrypointTestHelpers.FakeLibraryManager.Create(
            [],
            _ => [],
            id => items.FirstOrDefault(item => item.Id == id),
            getCollectionFolders: _ => collectionFolders);

    private static AnalysisResult CreateResult(
        JobSnapshot lease,
        Guid itemId,
        string fileVersion,
        AnalysisStatus status = AnalysisStatus.CompletedWithSegments,
        bool productionApproved = true,
        RemoteMode mode = RemoteMode.Introduction)
        => new()
        {
            AnalysisId = Guid.CreateVersion7(),
            JobId = lease.JobId,
            AttemptId = lease.AttemptId,
            ServerId = lease.ServerId,
            ItemId = itemId,
            MediaSourceKey = "/media/series/s01e01.mkv",
            SourcePath = "/media/series/s01e01.mkv",
            FileVersionToken = fileVersion,
            ExpectedItemModeRevision = lease.Items.Single(item => item.ItemId == itemId).ExpectedModeRevisions[mode],
            Mode = mode,
            Status = status,
            TimelineEndTicks = TimeSpan.FromMinutes(4).Ticks,
            Detector = new DetectorIdentity
            {
                Id = "test",
                ProfileHash = mode == RemoteMode.Introduction ? lease.IntroductionProfileHash : lease.CreditsProfileHash,
                ProductionApproved = productionApproved,
            },
            Segments = status == AnalysisStatus.CompletedWithSegments
                ?
                [
                    new SegmentCandidate
                    {
                        CandidateId = "candidate",
                        Type = mode,
                        StartTicks = 100,
                        EndTicks = 200,
                        Decision = CandidateDecision.Accept,
                    },
                ]
                : [],
            SearchedRanges = status == AnalysisStatus.CompletedNoSegments
                ? [new SegmentScanner.Contracts.TimeRange(0, TimeSpan.FromMinutes(4).Ticks)]
                : [],
        };

    private class RemoteHostProxy : DispatchProxy
    {
        private string _systemId = string.Empty;

        public static IServerApplicationHost Create()
        {
            var proxy = Create<IServerApplicationHost, RemoteHostProxy>();
            ((RemoteHostProxy)(object)proxy)._systemId = PluginId;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == $"get_{nameof(IServerApplicationHost.SystemId)}"
                ? _systemId
                : throw new NotImplementedException(targetMethod?.Name);
    }
}
