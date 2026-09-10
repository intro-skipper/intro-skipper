// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestLegacyAnalysisCompatibility
{
    [Theory]
    [InlineData(AnalysisMode.Introduction, false, false, "D65089C5988650A1")]
    [InlineData(AnalysisMode.Introduction, true, false, "8DBCDDC0D7121940")]
    [InlineData(AnalysisMode.Credits, false, false, "21D032F917DBB286")]
    [InlineData(AnalysisMode.Credits, false, true, "F54591829B4EE31B")]
    [InlineData(AnalysisMode.Credits, true, false, "C7BDDBC293143FF9")]
    [InlineData(AnalysisMode.Credits, true, true, "E12710E4AFCAF91D")]
    [InlineData(AnalysisMode.Recap, false, false, "00AC4A136129A4A7")]
    [InlineData(AnalysisMode.Recap, true, false, "3E22D6A17CDA5178")]
    public void LegacyHashes_MatchOriginal1011Hasher(AnalysisMode mode, bool available, bool alternative, string expected)
    {
        Assert.Equal(expected, LegacyAnalysisCompatibility.AnalysisHash(new PluginConfiguration(), mode, AnalyzerAction.Default, available, alternative));
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction, false)]
    [InlineData(AnalysisMode.Credits, false)]
    [InlineData(AnalysisMode.Credits, true)]
    [InlineData(AnalysisMode.Recap, false)]
    public async Task ImportedCompletedSeason_AdoptsHashesWithoutDetection(AnalysisMode mode, bool alternative)
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "introskipper.db");
        var databasePath = Path.Combine(directory, "introskipper-v2.db");
        var mediaPath = Path.Combine(directory, "episode.mkv");
        await File.WriteAllBytesAsync(mediaPath, []);
        try
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var seasonId = Guid.NewGuid();
            var action = AnalyzerAction.Chapter;
            var legacyHash = LegacyAnalysisCompatibility.AnalysisHash(config, mode, action, true, alternative);
            var currentHash = ConfigHasher.Analysis(config, mode, action, true);
            var episodeIdsJson = JsonSerializer.Serialize(ids);
            LegacySchemaFixtures.CreateV5(
                legacyPath,
                [new(ids[0], 10, 60, (int)mode, ConfigHash: legacyHash), new(ids[2], 15, 65, (int)mode, IsUserProvided: true)],
                [new(seasonId, (int)mode, (int)action, episodeIdsJson, legacyHash, episodeIdsJson)]);
            var legacyBytes = await File.ReadAllBytesAsync(legacyPath);
            var database = DatabaseTestHelpers.CreateSegmentDatabase(databasePath);
            await database.InitializeAsync();
            var original = Assert.Single(await database.GetSegmentsAsync(ids[0]));
            database = DatabaseTestHelpers.CreateSegmentDatabase(databasePath);
            var items = ids.Select(id => JellyfinItems.Episode(id, Guid.NewGuid(), seasonId, path: mediaPath)).ToArray();
            var library = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], items);
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);
            var ffmpeg = new StubFFmpegService { VersionCheck = () => true };
            var queue = new QueueManager(NullLogger<QueueManager>.Instance, library, null!, null!, ffmpeg, database);
            var candidates = ids.Select(id => new QueuedEpisode
            {
                EpisodeId = id,
                SeasonId = seasonId,
                SeasonNumber = 1,
                DateAdded = DateTime.UtcNow.AddDays(-30),
            }).ToArray();

            var verified = await queue.VerifyQueueAsync(candidates, [mode]);

            Assert.Equal(3, verified.Count);
            Assert.Equal(EpisodeState.Analyzed, verified[0].GetAnalyzed(mode));
            Assert.Equal(EpisodeState.NoSegments, verified[1].GetAnalyzed(mode));
            Assert.Equal(EpisodeState.UserProvided, verified[2].GetAnalyzed(mode));
            var states = await database.GetSettleReanalysisStatesAsync(seasonId);
            Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(verified, config, DateTime.UtcNow));
            Assert.Empty(SeasonReanalysisPlanner.GetSettleReanalysisModes(states, ids, [mode], true));
            var task = new BaseItemAnalyzerTask(NullLogger.Instance, NullLoggerFactory.Instance, null!, ffmpeg, null!, database);
            await task.AnalyzeItemsAsync(verified, mode, action, true, CancellationToken.None);
            Assert.Equal(0, ffmpeg.FingerprintCalls);
            Assert.Equal(0, ffmpeg.CreditsScanCalls);
            Assert.Equal(0, ffmpeg.RangeScanCalls);
            Assert.Equal(0, ffmpeg.VisualScanCalls);
            Assert.Equal(0, ffmpeg.IntervalScanCalls);

            var segment = Assert.Single(await database.GetSegmentsAsync(ids[0]));
            Assert.Equal(original.Id, segment.Id);
            Assert.Equal(original.StartTicks, segment.StartTicks);
            Assert.Equal(original.EndTicks, segment.EndTicks);
            Assert.Equal(original.UpdatedAt, segment.UpdatedAt);
            Assert.Equal(SegmentSource.Unknown, segment.Source);
            Assert.Equal(currentHash, segment.ConfigHash);
            Assert.Equal(0, await database.CleanStaleAutomaticSegmentsAsync(ids, mode, currentHash));
            var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonId, ids);
            Assert.All(snapshot.AnalyzedConfigHashes.Values, hash => Assert.Equal(currentHash, hash));
            Assert.False(await LegacyAnalysisCompatibility.UpgradeAsync(database, snapshot, config));
            Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacyPath));

            await using var db = DatabaseTestHelpers.CreateSegmentContext(databasePath);
            Assert.Empty(await db.ProjectionQueue.ToListAsync());
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(databasePath);
            DatabaseTestHelpers.DeleteSqliteFiles(legacyPath);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction, "language")]
    [InlineData(AnalysisMode.Introduction, "channels")]
    [InlineData(AnalysisMode.Introduction, "duration")]
    [InlineData(AnalysisMode.Introduction, "action")]
    [InlineData(AnalysisMode.Credits, "legacy")]
    [InlineData(AnalysisMode.Credits, "threshold")]
    [InlineData(AnalysisMode.Recap, "cold-open")]
    public async Task ChangedSettings_DoNotAdoptLegacyCompletion(AnalysisMode mode, string change)
    {
        using var temp = new TempSegmentDb();
        var config = new PluginConfiguration();
        var id = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var legacyHash = LegacyAnalysisCompatibility.AnalysisHash(config, mode, AnalyzerAction.Default, true, false);
        await temp.Database.MarkItemsAnalyzedAsync(mode, [id], legacyHash);
        switch (change)
        {
            case "language": config.PreferredAudioLanguage = "eng"; break;
            case "channels": config.PreferAudioStreamWithMostChannels = false; break;
            case "duration": config.MinimumIntroDuration++; break;
            case "action": await temp.Database.SetAnalyzerActionAsync(seasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [mode] = AnalyzerAction.Chapter }); break;
            case "legacy": config.UseLegacyBlackFrameAnalyzer = true; break;
            case "threshold": config.BlackFrameThreshold++; break;
            case "cold-open": config.AnchorRecapToColdOpen = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        var snapshot = await temp.Database.GetSeasonQueueSnapshotAsync(seasonId, [id]);

        Assert.False(await LegacyAnalysisCompatibility.UpgradeAsync(temp.Database, snapshot, config));
        var candidate = new QueuedEpisode { EpisodeId = id };
        new QueueVerifier(config, [mode], snapshot, true).Classify(candidate);
        Assert.Equal(EpisodeState.NotAnalyzed, candidate.GetAnalyzed(mode));
    }

    [Theory]
    [InlineData(false, false, EpisodeState.NoSegments)]
    [InlineData(false, true, EpisodeState.NotAnalyzed)]
    [InlineData(true, false, EpisodeState.NoSegments)]
    [InlineData(true, true, EpisodeState.NoSegments)]
    public async Task CapabilityChanges_KeepExistingInvalidationDirection(bool previousAvailable, bool available, EpisodeState expected)
    {
        using var temp = new TempSegmentDb();
        var config = new PluginConfiguration();
        var id = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        const AnalysisMode mode = AnalysisMode.Introduction;
        await temp.Database.MarkItemsAnalyzedAsync(mode, [id], LegacyAnalysisCompatibility.AnalysisHash(config, mode, AnalyzerAction.Default, previousAvailable, false));
        var snapshot = await temp.Database.GetSeasonQueueSnapshotAsync(seasonId, [id]);
        Assert.True(await LegacyAnalysisCompatibility.UpgradeAsync(temp.Database, snapshot, config));
        snapshot = await temp.Database.GetSeasonQueueSnapshotAsync(seasonId, [id]);
        var candidate = new QueuedEpisode { EpisodeId = id };

        new QueueVerifier(config, [mode], snapshot, available).Classify(candidate);

        Assert.Equal(expected, candidate.GetAnalyzed(mode));
        config.MinimumIntroDuration++;
        Assert.False(await LegacyAnalysisCompatibility.UpgradeAsync(temp.Database, snapshot, config));
        candidate = new QueuedEpisode { EpisodeId = id };
        new QueueVerifier(config, [mode], snapshot, true).Classify(candidate);
        Assert.Equal(EpisodeState.NotAnalyzed, candidate.GetAnalyzed(mode));
    }

    [Fact]
    public async Task UnknownHashesAndUnrecordedModes_RemainPending()
    {
        using var temp = new TempSegmentDb();
        var config = new PluginConfiguration();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        const AnalysisMode mode = AnalysisMode.Introduction;
        await temp.Database.MarkItemsAnalyzedAsync(mode, [ids[0]], "unrecognized-hash");
        await temp.Database.MarkItemsAnalyzedAsync(mode, [ids[1]], string.Empty);
        var snapshot = await temp.Database.GetSeasonQueueSnapshotAsync(Guid.NewGuid(), ids);

        Assert.False(await LegacyAnalysisCompatibility.UpgradeAsync(temp.Database, snapshot, config));

        foreach (var id in ids)
        {
            var candidate = new QueuedEpisode { EpisodeId = id };
            new QueueVerifier(config, [mode, AnalysisMode.Recap], snapshot, true).Classify(candidate);
            Assert.Equal(EpisodeState.NotAnalyzed, candidate.GetAnalyzed(mode));
            Assert.Equal(EpisodeState.NotAnalyzed, candidate.GetAnalyzed(AnalysisMode.Recap));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleSnapshot_DoesNotRestoreResetOrReanalyzedRecords(bool reanalyzed)
    {
        using var temp = new TempSegmentDb();
        var config = new PluginConfiguration();
        var id = Guid.NewGuid();
        const AnalysisMode mode = AnalysisMode.Introduction;
        var legacyHash = LegacyAnalysisCompatibility.AnalysisHash(config, mode, AnalyzerAction.Default, true, false);
        await temp.Database.MarkItemsAnalyzedAsync(mode, [id], legacyHash);
        var snapshot = await temp.Database.GetSeasonQueueSnapshotAsync(Guid.NewGuid(), [id]);
        await temp.Database.ResetItemsForReanalysisAsync([id], [mode]);
        if (reanalyzed)
        {
            await temp.Database.MarkItemsAnalyzedAsync(mode, [id], "newer-analysis");
        }

        Assert.False(await LegacyAnalysisCompatibility.UpgradeAsync(temp.Database, snapshot, config));

        await using var db = temp.Context();
        var rows = await db.AnalyzedItems.ToListAsync();
        if (reanalyzed)
        {
            Assert.Equal("newer-analysis", Assert.Single(rows).ConfigHash);
        }
        else
        {
            Assert.Empty(rows);
        }
    }

    [Fact]
    public async Task HashAdoption_UpdatesOnlyMatchingActiveAutomaticSegmentsAndIsAtomic()
    {
        using var temp = new TempSegmentDb();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        const AnalysisMode mode = AnalysisMode.Credits;
        await temp.Database.MarkItemsAnalyzedAsync(mode, [id], "old");
        await using var db = temp.Context();
        var automatic = new DbSegment(id, mode, 10, 20, SegmentSource.Unknown, "old");
        var derived = new DbSegment(id, AnalysisMode.Preview, 20, 30, SegmentSource.CreditsDerived, "old");
        DbSegment[] untouched =
        [
            new(id, mode, 30, 40, SegmentSource.User, "old"),
            new(id, mode, 40, 50, SegmentSource.Unknown, "old") { State = SegmentState.Suppressed },
            new(id, mode, 50, 60, SegmentSource.Unknown, "different"),
            new(id, AnalysisMode.Introduction, 0, 10, SegmentSource.Unknown, "old"),
            new(otherId, mode, 10, 20, SegmentSource.Unknown, "old"),
        ];
        db.Segments.AddRange([automatic, derived, .. untouched]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailAdoption BEFORE UPDATE ON AnalyzedItems BEGIN SELECT RAISE(ABORT, 'test failure'); END");

        await Assert.ThrowsAsync<SqliteException>(() => temp.Database.UpgradeAnalysisHashAsync(mode, [id, otherId], "old", "current"));

        Assert.Equal("old", (await db.Segments.SingleAsync(s => s.Id == automatic.Id)).ConfigHash);
        Assert.Equal("old", (await db.AnalyzedItems.SingleAsync()).ConfigHash);
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailAdoption");
        Assert.Equal(1, await temp.Database.UpgradeAnalysisHashAsync(mode, [id, otherId], "old", "current"));
        var rows = await db.Segments.AsNoTracking().ToDictionaryAsync(s => s.Id);
        Assert.Equal("current", rows[automatic.Id].ConfigHash);
        Assert.Equal("current", rows[derived.Id].ConfigHash);
        Assert.All(untouched, original => Assert.Equal(original.ConfigHash, rows[original.Id].ConfigHash));
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }
}
