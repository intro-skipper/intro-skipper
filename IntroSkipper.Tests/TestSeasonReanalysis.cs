// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSeasonReanalysisPlanner
{
    private const int DefaultDelay = PluginConfiguration.DefaultSettledSeasonDelayHours;
    private const int Minimum = SeasonReanalysisPlanner.MinimumEpisodes;

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AnalyzeItemsAsync_EmptySeasonSet_DoesNotEnumerateLibrary()
    {
        // The test proxy throws on enumeration calls such as GetVirtualFolders.
        var libraryManager = EntrypointTestHelpers.CreateLibraryManager();
        var progress = new RecordingProgress();
        var analyzer = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateSeasonResolver(libraryManager),
            ffmpegService: null!,
            cacheService: DatabaseTestHelpers.CreateTempCacheService(),
            cacheDatabase: null!,
            database: DatabaseTestHelpers.CreateTempSegmentDatabase());

        await analyzer.AnalyzeItemsAsync(
            progress,
            CancellationToken.None,
            []);

        Assert.Equal(100, progress.Value);
    }

    // Columns: enabled, episode count, hours since the newest episode was added, configured
    // delay, category, season number, AnalyzeSeasonZero, IsExcluded, expected.
    [Theory]
    [InlineData(false, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum, 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay - 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum, 47, 48, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, 48, 48, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum, 0, 0, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum - 1, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Movie, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, true, true)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 0, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 0, true, false, true)]
    public void IsSettledForReanalysis(
        bool enabled,
        int episodeCount,
        int hoursSinceNewest,
        int delayHours,
        QueuedMediaCategory category,
        int seasonNumber,
        bool analyzeSeasonZero,
        bool excluded,
        bool expected)
    {
        var config = new PluginConfiguration
        {
            ReanalyzeSettledSeasons = enabled,
            SettledSeasonDelayHours = delayHours,
            AnalyzeSeasonZero = analyzeSeasonZero,
        };
        var season = Season(episodeCount, Now.AddHours(-hoursSinceNewest), category, seasonNumber, excluded);

        Assert.Equal(expected, SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void EpisodeAvailabilityDate_IgnoresMetadataSaveTime()
    {
        var created = Now.AddDays(-30);
        var saved = Now.AddMinutes(-1);
        var episode = new Episode
        {
            DateCreated = created,
            DateLastSaved = saved,
        };

        Assert.Equal(created, SeasonResolver.EpisodeAvailabilityDate(episode));
    }

    [Fact]
    public void EpisodeAvailabilityDate_FallsBackToMetadataSaveTime_WhenCreatedIsMissing()
    {
        var saved = Now.AddMinutes(-1);
        var episode = new Episode
        {
            DateLastSaved = saved,
        };

        Assert.Equal(saved, SeasonResolver.EpisodeAvailabilityDate(episode));
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Default, false, false)]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Chromaprint, false, false)]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Chapter, false, true)]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Default, true, true)]
    [InlineData(AnalysisMode.Credits, AnalyzerAction.Default, false, true)]
    public void GetSettleReanalysisModes_SkipsIntroduction_WhenChromaprintUnavailable(
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        bool expected)
    {
        // A recorded empty episode set differs from the current one, so only the
        // analyzer's availability decides.
        var states = new Dictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>
        {
            [mode] = (action, new HashSet<Guid>()),
        };

        Assert.Equal(expected, SeasonReanalysisPlanner.GetSettleReanalysisModes(states, [Guid.NewGuid()], [mode], ffmpegValid).Contains(mode));
    }

    [Fact]
    public void ExpandSettledResetModesForDerivedSegments_AddsPreview_WhenCreditsGenerateAnimePreview()
    {
        var resetModes = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], true);

        Assert.Equal([AnalysisMode.Credits, AnalysisMode.Preview], resetModes);
    }

    [Fact]
    public void ExpandSettledResetModesForDerivedSegments_DoesNotAddPreview_WhenDisabledOrAlreadyPresent()
    {
        var disabled = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], false);
        var alreadyPresent = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments(
            [AnalysisMode.Credits, AnalysisMode.Preview],
            true);

        Assert.Equal([AnalysisMode.Credits], disabled);
        Assert.Equal([AnalysisMode.Credits, AnalysisMode.Preview], alreadyPresent);
    }

    private static List<QueuedEpisode> Season(
        int count,
        DateTime newestAdded,
        QueuedMediaCategory category = QueuedMediaCategory.Episode,
        int seasonNumber = 1,
        bool excluded = false)
    {
        var episodes = new List<QueuedEpisode>(count);
        for (var i = 0; i < count; i++)
        {
            episodes.Add(new QueuedEpisode
            {
                EpisodeId = Guid.NewGuid(),
                SeasonNumber = seasonNumber,
                Category = category,
                IsExcluded = excluded,
                DateAdded = newestAdded.AddHours(-i), // index 0 is the most recent, the Max
            });
        }

        return episodes;
    }
}

public sealed class TestSeasonReanalysisReset : IDisposable
{
    private static readonly AnalysisMode[] IntroductionAndRecap = [AnalysisMode.Introduction, AnalysisMode.Recap];

    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SettleReanalysisGuard_PersistsCompletedEpisodeSet()
    {
        var season = Guid.NewGuid();
        var firstFive = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var firstSix = firstFive.Append(Guid.NewGuid()).ToArray();
        var replacementFive = firstFive.Skip(1).Append(Guid.NewGuid()).ToArray();

        using (var db = _db.Context())
        {
            await db.Database.MigrateAsync();
        }

        var database = _db.Database;

        // Stays eligible until the work is explicitly recorded, so a failed reset is retried.
        Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive));

        await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstFive);
        Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // already done for this set
        Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, [.. firstFive.Reverse()])); // order-insensitive set match
        Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix)); // grew: eligible again
        Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, replacementFive)); // same count, different membership
        Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstFive)); // unrecorded mode stays eligible

        await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstSix);
        Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // shrank: eligible again
        Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix));

        // A fresh facade over the same file simulates a plugin restart: the recorded
        // episode sets must be read back from the database.
        var reopenedDatabase = _db.CreateDatabase();

        Assert.False(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstSix));
        Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstFive));
        Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Credits, firstFive));
    }

    [Fact]
    public async Task ResetItemsForReanalysisAsync_DeletesAutomaticSegmentsOfResetModes_PreservesUserRowsAndOtherModes()
    {
        var seasonId = Guid.NewGuid();
        var autoEpisode = Guid.NewGuid();
        var userEpisode = Guid.NewGuid();
        var mixedEpisode = Guid.NewGuid();
        var userPreviewEpisode = Guid.NewGuid();

        using (var db = _db.Context())
        {
            await db.Database.MigrateAsync();

            // Automatic intro: deleted.
            db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chapter));

            // An episode holding both an automatic and a user intro is UserProvided for
            // the mode: the analyzers skip it, so the reset must keep its automatic row
            // (nothing would regenerate it).
            db.Segments.Add(new DbSegment(mixedEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(60), SegmentSource.Chromaprint));
            db.Segments.Add(new DbSegment(mixedEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(300), TickConversions.FromSeconds(330), SegmentSource.User));

            // Tombstoned (user-deleted) automatic intro of a reset mode: survives the
            // reset so the deleted range stays gone after re-analysis.
            db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(60), TickConversions.FromSeconds(90), SegmentSource.Chapter)
            {
                State = SegmentState.Suppressed,
            });

            // User-provided intro: preserved.
            db.Segments.Add(new DbSegment(userEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.User));

            // Automatic recap on the same episode: a mode outside the reset, preserved.
            db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Recap, TickConversions.FromSeconds(40), TickConversions.FromSeconds(60), SegmentSource.Chapter));

            // Automatic credits and the preview derived from them: both deleted, the
            // preview through the derived-mode expansion. A subtitle preview is retained
            // until its analyzer finishes, and the user preview is preserved.
            db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Credits, TickConversions.FromSeconds(1000), TickConversions.FromSeconds(1100), SegmentSource.Chapter));
            db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Preview, TickConversions.FromSeconds(1100), TickConversions.FromSeconds(1320), SegmentSource.CreditsDerived));
            db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Preview, TickConversions.FromSeconds(1060), TickConversions.FromSeconds(1090), SegmentSource.Subtitle));
            db.Segments.Add(new DbSegment(userPreviewEpisode, AnalysisMode.Preview, TickConversions.FromSeconds(1100), TickConversions.FromSeconds(1320), SegmentSource.User));

            db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Chromaprint));
            db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Recap, AnalyzerAction.Default));
            db.AnalyzedItems.AddRange(
                new DbAnalyzedItem(autoEpisode, AnalysisMode.Introduction, "hash"),
                new DbAnalyzedItem(userEpisode, AnalysisMode.Introduction, "hash"),
                new DbAnalyzedItem(mixedEpisode, AnalysisMode.Introduction, "hash"),
                new DbAnalyzedItem(autoEpisode, AnalysisMode.Recap, "recap-hash"),
                new DbAnalyzedItem(autoEpisode, AnalysisMode.Credits, "credits-hash"),
                new DbAnalyzedItem(userPreviewEpisode, AnalysisMode.Credits, "credits-hash"),
                new DbAnalyzedItem(autoEpisode, AnalysisMode.Preview, "preview-hash"),
                new DbAnalyzedItem(userPreviewEpisode, AnalysisMode.Preview, "preview-hash"));

            await db.SaveChangesAsync();
        }

        var cacheDbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
        DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath).Upsert(autoEpisode, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, string.Empty);

        IReadOnlyCollection<AnalysisMode> resetModes = [
            AnalysisMode.Introduction,
            .. SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], true)
        ];
        await _db.Database.ResetItemsForReanalysisAsync(
            [autoEpisode, userEpisode, mixedEpisode, userPreviewEpisode],
            resetModes,
            preserveSubtitleModes: [AnalysisMode.Preview]);

        // The reset reuses the cached fingerprints; only the derived results go.
        using (var cacheDb = DatabaseTestHelpers.CreateCacheContext(cacheDbPath))
        {
            Assert.True(cacheDb.DetectionCache.Any(e => e.ItemId == autoEpisode && e.Mode == AnalysisMode.Introduction));
        }

        using (var db = _db.Context())
        {
            Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Active));
            Assert.True(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Suppressed));
            Assert.True(db.Segments.Any(s => s.ItemId == userEpisode && s.Type == AnalysisMode.Introduction && s.Source == SegmentSource.User));
            Assert.True(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Recap));
            Assert.Equal(2, db.Segments.Count(s => s.ItemId == mixedEpisode && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Active));
            Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Credits));
            Assert.Single(db.Segments, s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Preview && s.Source == SegmentSource.Subtitle);
            Assert.True(db.Segments.Any(s => s.ItemId == userPreviewEpisode && s.Type == AnalysisMode.Preview && s.Source == SegmentSource.User));

            // The reset modes' records are gone (every item is NotAnalyzed again); the
            // season's action and the other mode's record survive.
            var recap = Assert.Single(await db.AnalyzedItems.ToListAsync());
            Assert.Equal((autoEpisode, AnalysisMode.Recap, "recap-hash"), (recap.ItemId, recap.Type, recap.ConfigHash));
            var state = await db.SeasonStates.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
            Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
        }
    }

    [Fact]
    public async Task SeasonStateAndAnalysisRecords_UpsertWithoutClobberingEachOther()
    {
        var seasonId = Guid.NewGuid();
        var episodeA = Guid.NewGuid();
        var episodeB = Guid.NewGuid();
        var completedEpisodeIds = new[] { episodeA, episodeB, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var lowerEpisodeIds = new[] { episodeA, episodeB, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        using (var db = _db.Context())
        {
            await db.Database.MigrateAsync();
        }

        var database = _db.Database;
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [episodeA, episodeB], "hash-a");
        await database.SetAnalyzerActionAsync(
            seasonId,
            new Dictionary<AnalysisMode, AnalyzerAction>
            {
                [AnalysisMode.Introduction] = AnalyzerAction.Chromaprint,
            });
        await database.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], completedEpisodeIds);
        await database.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], lowerEpisodeIds);
        await database.ResetItemsForReanalysisAsync([episodeA, Guid.NewGuid()], [AnalysisMode.Introduction]);

        using (var db = _db.Context())
        {
            var state = await db.SeasonStates.SingleAsync();
            Assert.Equal(seasonId, state.SeasonId);
            Assert.Equal(AnalysisMode.Introduction, state.Type);
            Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
            Assert.Equal(lowerEpisodeIds, state.SettledReanalysisEpisodeIds);

            var analyzed = await db.AnalyzedItems.SingleAsync();
            Assert.Equal(episodeB, analyzed.ItemId);
            Assert.Equal("hash-a", analyzed.ConfigHash);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_ReopensNoSegments_WhenChromaprintBecomesAvailable()
    {
        var config = new PluginConfiguration();
        using var fixture = new VerifyQueueFixture(config);
        var withoutChromaprintHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: false);

        using (var db = DatabaseTestHelpers.CreateSegmentContext(fixture.DbPath))
        {
            await db.Database.MigrateAsync();
            db.SeasonStates.Add(new DbSeasonState(fixture.SeasonId, AnalysisMode.Introduction, AnalyzerAction.Default));
            db.AnalyzedItems.Add(new DbAnalyzedItem(fixture.EpisodeId, AnalysisMode.Introduction, withoutChromaprintHash));
            await db.SaveChangesAsync();
        }

        var stillUnavailable = await fixture.VerifyAsync(ffmpegValid: false);
        Assert.Equal(EpisodeState.NoSegments, stillUnavailable.Single().GetAnalyzed(AnalysisMode.Introduction));

        var reopened = await fixture.VerifyAsync(ffmpegValid: true);
        Assert.Equal(EpisodeState.NotAnalyzed, reopened.Single().GetAnalyzed(AnalysisMode.Introduction));
    }

    // The record's hash matches directly with Chromaprint available and through the
    // availability fallback without it; a changed file must win over both.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyQueueAsync_ReopensItem_WhenFileVersionChanged(bool ffmpegValid)
    {
        var config = new PluginConfiguration();
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var hash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true);
        await fixture.CreateDatabase().MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)1)], hash);

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid));

        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.True(episode.FileChanged);
        Assert.Equal(2, episode.FileVersion);
    }

    [Fact]
    public async Task VerifyQueueAsync_FlagsChangedFile_FromRecordOfAModeNotInTheRun()
    {
        var config = new PluginConfiguration();
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var introHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true);
        var database = fixture.CreateDatabase();
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)null)], introHash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [(fixture.EpisodeId, (long?)1)], "credits-hash");

        // The run covers Introduction only; the Credits record is what proves the file changed.
        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: true));

        Assert.True(episode.FileChanged);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));

        // The unversioned Introduction record is not backfilled: the reset rewrites it.
        using var verifyDb = DatabaseTestHelpers.CreateSegmentContext(fixture.DbPath);
        Assert.Null((await verifyDb.AnalyzedItems.AsNoTracking().SingleAsync(a => a.Type == AnalysisMode.Introduction)).FileVersion);
    }

    [Theory]
    [InlineData(2L)]
    [InlineData(null)]
    public async Task VerifyQueueAsync_KeepsItemSettled_WhenFileVersionMatchesOrIsUnknown(long? recordedVersion)
    {
        var config = new PluginConfiguration();
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var hash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true);
        var database = fixture.CreateDatabase();
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, recordedVersion)], hash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [(fixture.EpisodeId, recordedVersion)], hash);

        // A text sidecar is read only by subtitle detection, which is off here.
        fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nPreviously on\n");

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: true));

        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.False(episode.FileChanged);

        // A record without a version is stamped for every mode, so a later replacement
        // of the file is noticed even for modes this run did not cover.
        using var verifyDb = DatabaseTestHelpers.CreateSegmentContext(fixture.DbPath);
        Assert.All(await verifyDb.AnalyzedItems.AsNoTracking().ToListAsync(), record => Assert.Equal(2, record.FileVersion));
    }

    /// <summary>
    /// A rewritten text sidecar changes what subtitle detection reads, not the media, so it
    /// reopens only the Recap mode that reads it. A record made while subtitle detection was
    /// on carries the sidecars in its version, which still reopens only Recap after the
    /// detection is turned off, even when a sidecar changes before Recap is recorded again.
    /// No case flags the file as changed, which would reset the item's other modes and
    /// discard its fingerprints. The sidecar version is compared even when Jellyfin holds
    /// no media write time (file version 0 here), including when the last sidecar is removed.
    /// The sidecar version cannot tell a sidecar edit from a replaced media file, so Recap
    /// reruns subtitle detection alone only while the Introduction record confirms the media
    /// version. Without a media write time nothing can, and the mode runs its full chain.
    /// </summary>
    [Theory]
    [InlineData(false, "rewrite", 2L, true)]
    [InlineData(false, "rewrite", 0L, false)]
    [InlineData(false, "delete", 0L, false)]
    [InlineData(true, "none", 2L, true)]
    [InlineData(true, "rewrite", 2L, true)]
    public async Task VerifyQueueAsync_SubtitleDetectingRecord_ReopensOnlyItsMode(bool turnDetectionOff, string sidecarChange, long fileVersion, bool subtitleOnly)
    {
        var config = SubtitleRecapConfig();
        using var fixture = new VerifyQueueFixture(config, fileVersion);
        var sidecar = fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nPreviously on\n");
        await fixture.SettleIntroductionAndRecapAsync();
        var settled = Assert.Single(await fixture.VerifyAsync(ffmpegValid: false, IntroductionAndRecap));
        Assert.Equal(EpisodeState.NoSegments, settled.GetAnalyzed(AnalysisMode.Recap));

        config.EnableSubtitleRecapDetection = !turnDetectionOff;
        if (sidecarChange == "rewrite")
        {
            await File.AppendAllTextAsync(sidecar, "\n2\n00:00:03,000 --> 00:00:04,000\nA new line\n");
        }
        else if (sidecarChange == "delete")
        {
            File.Delete(sidecar);
        }

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: false, IntroductionAndRecap));

        Assert.False(episode.FileChanged);
        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Recap));
        Assert.Equal(subtitleOnly, episode.NeedsSubtitleOnlyReanalysis(AnalysisMode.Recap));
    }

    // A record made while Chromaprint was available settles through the availability
    // fallback when this pass's probe fails. A rewritten sidecar must reopen Recap all the
    // same, or the new sidecar version would be recorded without being read.
    [Fact]
    public async Task VerifyQueueAsync_SidecarRewrite_ReopensRecapWhenTheChromaprintProbeFails()
    {
        using var fixture = new VerifyQueueFixture(SubtitleRecapConfig(), fileVersion: 2);
        var sidecar = fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nPreviously on\n");
        await fixture.SettleIntroductionAndRecapAsync(chromaprintAvailable: true);
        await File.AppendAllTextAsync(sidecar, "\n2\n00:00:03,000 --> 00:00:04,000\nA new line\n");

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: false, IntroductionAndRecap));

        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Recap));
    }

    // The sidecar version differs and the conventional settings changed too, so the stored
    // chapter recap is not current and Recap runs its full chain instead of subtitle detection
    // alone. The chapter recap is longer than the new maximum, so none remains. The version
    // differs because the sidecar was rewritten, or because detection was turned on, which
    // records the sidecar version where the media version stood.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeItemsAsync_SidecarAndConventionalSettingsChange_RunsTheFullChain(bool detectionOnBefore)
    {
        var config = SubtitleRecapConfig();
        config.EnableSubtitleRecapDetection = detectionOnBefore;
        config.DetectRecapUsingBlackFrames = false;
        config.MaximumRecapDuration = 120;
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2, RecapChapters());
        var sidecar = fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var database = fixture.CreateDatabase();
        var ffmpeg = new StubFFmpegService { VersionCheck = () => false, Subtitles = _ => new([], Complete: true), KeyFrames = (_, _, _) => [], Silence = (_, _, _) => [] };
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Contains(await database.GetSegmentsAsync(fixture.EpisodeId), s => s.Type == AnalysisMode.Recap && s.Source == SegmentSource.Chapter);

        config.MaximumRecapDuration = 60;
        config.EnableSubtitleRecapDetection = true;
        if (detectionOnBefore)
        {
            await File.AppendAllTextAsync(sidecar, "\n2\n00:00:03,000 --> 00:00:04,000\nA new line\n");
        }

        await fixture.AnalyzeAsync(ffmpeg);

        Assert.DoesNotContain(await database.GetSegmentsAsync(fixture.EpisodeId), s => s.Type == AnalysisMode.Recap);
    }

    // A sidecar rewrite of an unchanged file removed the cue behind a subtitle recap. The
    // analyzers ranked below subtitles never ran for the episode, so it gets the full chain
    // instead of subtitle detection alone, which reaches the black-frame fallback.
    [Fact]
    public async Task AnalyzeItemsAsync_SidecarRewriteBehindASubtitleRecap_RunsTheFullChain()
    {
        var config = SubtitleRecapConfig();
        config.DetectRecapUsingBlackFrames = true;
        ChapterInfo[] chapters = [new() { Name = "Cold open", StartPositionTicks = 0 }, new() { Name = "Story", StartPositionTicks = DatabaseTestHelpers.Ticks(60) }];
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2, chapters);
        var sidecar = fixture.WriteSidecar(".en.srt", "1\n00:00:10,000 --> 00:00:13,000\nPreviously on\n");
        var database = fixture.CreateDatabase();
        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => false, Subtitles = _ => new([new SubtitleCue(10, 13, "Previously on")], Complete: true), KeyFrames = (_, _, _) => [], Silence = (_, _, _) => [] });
        Assert.Equal(SegmentSource.Subtitle, Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId)).Source);

        await File.WriteAllTextAsync(sidecar, "1\n00:00:10,000 --> 00:00:13,000\nHello\n");
        var ffmpeg = new StubFFmpegService { VersionCheck = () => false, Subtitles = _ => new([], Complete: true), RangeBlackFrames = (_, _, _, _, _) => [], KeyFrames = (_, _, _) => [], Silence = (_, _, _) => [] };
        await fixture.AnalyzeAsync(ffmpeg);

        Assert.Contains(ffmpeg.Calls, call => call is StubFFmpegService.RangeScan);
        Assert.Empty(await database.GetSegmentsAsync(fixture.EpisodeId));
    }

    // In a library that scans only Previews, a replaced media file leaves the Preview record
    // the only one, and with no sidecars its sidecar version is the old media version. No
    // other record confirms the media version, so the item is reset like any replaced file
    // and Preview finds the new file's chapter instead of keeping the old file's.
    [Fact]
    public async Task AnalyzeItemsAsync_ReplacedFileInAPreviewOnlyLibrary_FindsTheNewChapter()
    {
        var config = PreviewOnlyConfig(scanRecap: false);
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2, PreviewChapters(200));
        var database = fixture.CreateDatabase();
        var hash = ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, ffmpegValid: true);
        await database.ReplaceAutoSegmentsAsync(fixture.EpisodeId, AnalysisMode.Preview, [new Segment(fixture.EpisodeId, new TimeRange(150, 200))], SegmentSource.Chapter, hash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Preview, [(fixture.EpisodeId, (long?)1)], hash);

        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => true, Subtitles = _ => new([], Complete: true) });

        var preview = Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId));
        Assert.Equal(200, TickConversions.ToSeconds(preview.StartTicks));
    }

    // Jellyfin's chapter table holds an intro chapter another plugin added, which the file
    // lacks, and the file holds its own. Each switch of the chapter source reruns Introduction
    // on the next pass against the source then in effect, which replaces the stored intro.
    [Fact]
    public async Task ChapterSourceSwitch_ReanalyzesFromTheSourceInEffect()
    {
        var config = ChapterSourceConfig();
        using var fixture = new VerifyQueueFixture(config, chapters: IntroChapters(20, 60));
        var database = fixture.CreateDatabase();
        var ffmpeg = ChapterSourceFfmpeg(IntroChapters(100, 140));

        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((20d, 60d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));

        config.UseEmbeddedChaptersForAnalysis = true;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((100d, 140d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));

        config.UseEmbeddedChaptersForAnalysis = false;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((20d, 60d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));
    }

    // The setting can change while a season's pass runs. The chapters were read once, so the
    // record names the source they came from, and the next pass under the new setting reruns.
    [Fact]
    public async Task ChapterSourceSwitch_DuringAPass_RecordsTheSourceTheChaptersCameFrom()
    {
        var config = ChapterSourceConfig();
        config.UseEmbeddedChaptersForAnalysis = true;
        using var fixture = new VerifyQueueFixture(config, chapters: IntroChapters(20, 60));
        var database = fixture.CreateDatabase();
        var ffmpeg = new StubFFmpegService
        {
            VersionCheck = () => false,
            EmbeddedChapters = _ =>
            {
                // The admin turns the setting off while the pass reads the file's chapters.
                config.UseEmbeddedChaptersForAnalysis = false;
                return IntroChapters(100, 140);
            },
            Silence = (_, _, _) => [],
            KeyFrames = (_, _, _) => [],
        };

        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((100d, 140d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));

        // The setting stays off. The record names the embedded source, so this pass reruns
        // Introduction from Jellyfin's table instead of keeping the embedded intro.
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((20d, 60d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));
    }

    // The setting goes off while the pass reads the file, and both sources hold the same
    // chapters. The record keeps naming the source the pass read, as the analysis records do,
    // so the pass under the new setting checks it once more and keeps the result.
    [Fact]
    public async Task ChapterSourceSwitch_DuringAPass_WithTheSameChapters_RecordsTheSourceRead()
    {
        var config = ChapterSourceConfig();
        using var fixture = new VerifyQueueFixture(config, chapters: IntroChapters(20, 60));
        var database = fixture.CreateDatabase();
        var ffmpeg = new StubFFmpegService
        {
            VersionCheck = () => false,
            EmbeddedChapters = _ =>
            {
                config.UseEmbeddedChaptersForAnalysis = false;
                return IntroChapters(20, 60);
            },
            Silence = (_, _, _) => [],
            KeyFrames = (_, _, _) => [],
        };
        string Hash(ChapterSource source) => ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: false, chapterSource: source);
        async Task<string> RecordHashAsync()
            => Assert.Single((await database.GetSeasonQueueSnapshotAsync(fixture.SeasonId, [fixture.EpisodeId])).AnalysisRecords).Value.ConfigHash;

        await fixture.AnalyzeAsync(ffmpeg);
        var intro = Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId));

        config.UseEmbeddedChaptersForAnalysis = true;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal(Hash(ChapterSource.Embedded), await RecordHashAsync());

        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal(Hash(ChapterSource.JellyfinTable), await RecordHashAsync());
        Assert.Equal(intro.Id, Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId)).Id);
    }

    // A Commercial row from a chapter only Jellyfin's table holds stays while Commercial
    // scanning is off at the switch. The first pass with Commercial on again reruns the mode
    // against the file's chapters, which have no commercial, and removes the row.
    [Fact]
    public async Task ChapterSourceSwitch_WhileCommercialIsOff_RetiresTheInjectedCommercialOnceItIsOn()
    {
        var config = ChapterSourceConfig();
        config.ScanCommercial = true;
        using var fixture = new VerifyQueueFixture(config, chapters: [Chapter("Episode", 0), Chapter("Advertisement", 100), Chapter("Story", 140)]);
        var database = fixture.CreateDatabase();
        var ffmpeg = ChapterSourceFfmpeg([Chapter("Episode", 0)]);
        async Task<DbSegment[]> CommercialsAsync()
            => [.. (await database.GetSegmentsAsync(fixture.EpisodeId)).Where(row => row.Type == AnalysisMode.Commercial && row.State == SegmentState.Active)];

        await fixture.AnalyzeAsync(ffmpeg);
        var commercial = Assert.Single(await CommercialsAsync());

        config.ScanCommercial = false;
        config.UseEmbeddedChaptersForAnalysis = true;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal(commercial.Id, Assert.Single(await CommercialsAsync()).Id);

        config.ScanCommercial = true;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Empty(await CommercialsAsync());
    }

    // Jellyfin's table holds the file's own chapters, as it does for a file no other plugin
    // touched. A switch probes the file once, finds the same chapters, re-stamps the record and
    // its segment, and runs no analyzer. The next pass reads nothing. The switch back probes
    // once more for the comparison and keeps the result again.
    [Fact]
    public async Task ChapterSourceSwitch_WithTheSameChaptersInBothSources_KeepsTheResult()
    {
        var config = ChapterSourceConfig();
        using var fixture = new VerifyQueueFixture(config, chapters: IntroChapters(20, 60));
        var database = fixture.CreateDatabase();
        var probes = 0;
        var silenceScans = 0;
        var ffmpeg = new StubFFmpegService
        {
            VersionCheck = () => false,
            EmbeddedChapters = _ =>
            {
                probes++;
                return IntroChapters(20, 60);
            },
            Silence = (_, _, _) =>
            {
                silenceScans++;
                return [];
            },
            KeyFrames = (_, _, _) => [],
        };
        string Hash(ChapterSource source) => ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: false, chapterSource: source);
        async Task<DbSegment> IntroAsync() => Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId));
        async Task<string> RecordHashAsync()
            => Assert.Single((await database.GetSeasonQueueSnapshotAsync(fixture.SeasonId, [fixture.EpisodeId])).AnalysisRecords).Value.ConfigHash;

        await fixture.AnalyzeAsync(ffmpeg);
        var intro = await IntroAsync();
        var scans = silenceScans;
        Assert.Equal((0, Hash(ChapterSource.JellyfinTable)), (probes, await RecordHashAsync()));

        config.UseEmbeddedChaptersForAnalysis = true;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((1, scans), (probes, silenceScans));
        Assert.Equal((intro.Id, Hash(ChapterSource.Embedded)), ((await IntroAsync()).Id, (await IntroAsync()).ConfigHash));
        Assert.Equal(Hash(ChapterSource.Embedded), await RecordHashAsync());

        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal(1, probes);

        config.UseEmbeddedChaptersForAnalysis = false;
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((2, scans), (probes, silenceScans));
        Assert.Equal((intro.Id, Hash(ChapterSource.JellyfinTable)), ((await IntroAsync()).Id, (await IntroAsync()).ConfigHash));
        Assert.Equal(Hash(ChapterSource.JellyfinTable), await RecordHashAsync());
    }

    // The setting goes off and ffprobe can no longer read the file for the comparison, so the
    // episode is analyzed again from Jellyfin's table.
    [Fact]
    public async Task ChapterSourceSwitch_WhenTheFileCannotBeProbedForTheComparison_Reanalyzes()
    {
        var config = ChapterSourceConfig();
        config.UseEmbeddedChaptersForAnalysis = true;
        using var fixture = new VerifyQueueFixture(config, chapters: IntroChapters(20, 60));
        var database = fixture.CreateDatabase();
        ChapterInfo[]? embedded = IntroChapters(100, 140);
        var ffmpeg = new StubFFmpegService
        {
            VersionCheck = () => false,
            EmbeddedChapters = _ => embedded,
            Silence = (_, _, _) => [],
            KeyFrames = (_, _, _) => [],
        };

        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal((100d, 140d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));

        config.UseEmbeddedChaptersForAnalysis = false;
        embedded = null;
        await fixture.AnalyzeAsync(ffmpeg);

        Assert.Equal((20d, 60d), await ActiveRangeAsync(database, fixture.EpisodeId, AnalysisMode.Introduction));
    }

    // A record under the other chapter source's hash settles the episode and flags the mode for
    // the pass's chapter source check, naming the stored hash and the current one. A record
    // written with Chromaprint available still settles while the ffmpeg probe fails, as a
    // current record would, but is not checked: the check could reopen the mode, and the chain
    // could not reproduce the Chromaprint result it would discard.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyQueueAsync_RecordFromTheOtherChapterSource_SettlesPendingTheCheck(bool ffmpegValid)
    {
        var config = ChapterSourceConfig();
        config.UseEmbeddedChaptersForAnalysis = true;
        using var fixture = new VerifyQueueFixture(config);
        var tableHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true, chapterSource: ChapterSource.JellyfinTable);
        var embeddedHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true);
        await fixture.CreateDatabase().MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)0)], tableHash);

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid));

        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Introduction));
        if (ffmpegValid)
        {
            Assert.Equal(new ChapterSourceCheck(tableHash, embeddedHash, ChapterSource.Embedded), Assert.Single(episode.ChapterSourceChecks).Value);
        }
        else
        {
            Assert.Empty(episode.ChapterSourceChecks);
        }
    }

    // A None action records episodes without an analyzer, so a record from the other chapter
    // source is re-recorded by the mode at no cost instead of costing a probe.
    [Fact]
    public async Task VerifyQueueAsync_NoneAction_HasNoChapterSourceCheck()
    {
        var config = ChapterSourceConfig();
        config.UseEmbeddedChaptersForAnalysis = true;
        using var fixture = new VerifyQueueFixture(config);
        var database = fixture.CreateDatabase();
        await database.SetAnalyzerActionAsync(fixture.SeasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.None });
        var tableHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.None, ffmpegValid: true, chapterSource: ChapterSource.JellyfinTable);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)0)], tableHash);

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: true));

        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(episode.ChapterSourceChecks);
    }

    // A user segment settles the mode whatever its record says, so a record from the other
    // chapter source on such an episode is neither checked nor probed.
    [Fact]
    public async Task VerifyQueueAsync_UserProvidedEpisode_HasNoChapterSourceCheck()
    {
        var config = ChapterSourceConfig();
        config.UseEmbeddedChaptersForAnalysis = true;
        using var fixture = new VerifyQueueFixture(config);
        var database = fixture.CreateDatabase();
        var tableHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true, chapterSource: ChapterSource.JellyfinTable);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)0)], tableHash);
        await database.SeedUserSegmentAsync(fixture.EpisodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(10), DatabaseTestHelpers.Ticks(40));

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: true));

        Assert.Equal(EpisodeState.UserProvided, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(episode.ChapterSourceChecks);
    }

    // A Recap-only library recorded a chapter recap with subtitle detection on, then the
    // media file was replaced by one without a recap. Whether detection stays on or was
    // turned off since, the Recap record cannot tell the new file from a sidecar change and
    // nothing else confirms the media version, so the old file's recap must not survive.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeItemsAsync_ReplacedFileInARecapOnlyLibrary_DropsTheOldFilesRecap(bool detectionStaysOn)
    {
        var config = new PluginConfiguration
        {
            ScanIntroduction = false,
            ScanCredits = false,
            ScanRecap = true,
            ScanPreview = false,
            ScanCommercial = false,
            ReanalyzeSettledSeasons = false,
            DetectRecapUsingBlackFrames = false,
            EnableSubtitleRecapDetection = true,
        };
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var database = fixture.CreateDatabase();
        var hash = ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, ffmpegValid: false);
        await database.ReplaceAutoSegmentsAsync(fixture.EpisodeId, AnalysisMode.Recap, [new Segment(fixture.EpisodeId, new TimeRange(0, 60))], SegmentSource.Chapter, hash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Recap, [(fixture.EpisodeId, (long?)1)], hash);
        config.EnableSubtitleRecapDetection = detectionStaysOn;

        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => false, Subtitles = _ => new([], Complete: true) });

        Assert.DoesNotContain(await database.GetSegmentsAsync(fixture.EpisodeId), s => s.Type == AnalysisMode.Recap);
    }

    // A Recap record of a mode the season stopped running (its action is None) proves the
    // media file was replaced, though the Preview record, which records the sidecar version,
    // cannot. The item is reset, and Preview finds the new file's chapter.
    [Fact]
    public async Task AnalyzeItemsAsync_ReplacedFileProvenByTheRecapRecord_ResetsTheItem()
    {
        var config = PreviewOnlyConfig(scanRecap: true);
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2, PreviewChapters(200));
        var database = fixture.CreateDatabase();
        await database.SetAnalyzerActionAsync(fixture.SeasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Recap] = AnalyzerAction.None });
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Preview, [(fixture.EpisodeId, (long?)1)], ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, ffmpegValid: true));
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Recap, [(fixture.EpisodeId, (long?)1)], ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.None, ffmpegValid: true));

        Assert.True(Assert.Single(await fixture.VerifyAsync(ffmpegValid: true, [AnalysisMode.Recap, AnalysisMode.Preview])).FileChanged);
        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => true, Subtitles = _ => new([], Complete: true) });

        Assert.Equal(SegmentSource.Chapter, Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId), s => s.Type == AnalysisMode.Preview).Source);
    }

    // 12.0.5.0 derived a preview from the end of the first credits run to the start of the
    // next; previews now run from the end of the last credits run. A season upgraded with
    // such a preview re-derives it once under the new rule.
    [Fact]
    public async Task AnalyzeItemsAsync_PreviewDerivedBy12050_ReDerivesFromTheLastCreditsRun()
    {
        var config = new PluginConfiguration { ScanIntroduction = false, ScanCredits = true, ScanRecap = false, ScanPreview = true, ScanCommercial = false, ReanalyzeSettledSeasons = false };
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var database = fixture.CreateDatabase();
        await database.SetAnalysisOverridesAsync(fixture.SeasonId, null, null, previewFromCreditsEnd: true, null, null);

        // The hashes 12.0.5.0 wrote for this configuration.
        const string CreditsHash = "4D98C93D2975E2EE";
        const string PreviewHash = "2735F6E29B514900";
        await database.ReplaceAutoSegmentsAsync(fixture.EpisodeId, AnalysisMode.Credits, [new Segment(fixture.EpisodeId, new TimeRange(120, 150)), new Segment(fixture.EpisodeId, new TimeRange(200, 205))], SegmentSource.Chromaprint, CreditsHash);
        await database.ReplaceAutoSegmentsAsync(fixture.EpisodeId, AnalysisMode.Preview, [new Segment(fixture.EpisodeId, new TimeRange(150, 200))], SegmentSource.CreditsDerived, PreviewHash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [(fixture.EpisodeId, (long?)2)], CreditsHash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Preview, [(fixture.EpisodeId, (long?)2)], PreviewHash);

        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => true });

        var preview = Assert.Single(await database.GetSegmentsAsync(fixture.EpisodeId), s => s.Type == AnalysisMode.Preview);
        Assert.Equal(205, TickConversions.ToSeconds(preview.StartTicks), 1);
    }

    // A season override turns subtitle recap detection on against the plugin-wide setting.
    // The first pass reads the subtitles once and the next one settles without reading them
    // again. Clearing the override returns the season to the plugin-wide setting, and Recap is
    // recorded again under the detection-off hash, still without reading subtitles.
    [Fact]
    public async Task AnalyzeItemsAsync_SeasonSubtitleOverride_SettlesAndFollowsItsRemoval()
    {
        var config = SubtitleRecapConfig();
        config.EnableSubtitleRecapDetection = false;
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var database = fixture.CreateDatabase();
        await database.SetAnalysisOverridesAsync(fixture.SeasonId, null, null, null, subtitleRecapDetection: true, subtitlePreviewDetection: null);
        var extractions = 0;
        var ffmpeg = new StubFFmpegService
        {
            VersionCheck = () => false,
            Subtitles = _ =>
            {
                extractions++;
                return new([], Complete: true);
            },
        };

        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal(1, extractions);
        await fixture.AnalyzeAsync(ffmpeg);
        Assert.Equal(1, extractions);

        await database.SetAnalysisOverridesAsync(fixture.SeasonId, null, null, null, null, null);
        await fixture.AnalyzeAsync(ffmpeg);
        await fixture.AnalyzeAsync(ffmpeg);

        Assert.Equal(1, extractions);
        var record = (await database.GetSeasonQueueSnapshotAsync(fixture.SeasonId, [fixture.EpisodeId])).AnalysisRecords[(fixture.EpisodeId, AnalysisMode.Recap)];
        Assert.Equal(ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, ffmpegValid: false), record.ConfigHash);
    }

    // A Recap or Preview record is the only evidence a Recap-only library has that a file
    // was replaced, and it still is once the library stops scanning the mode. With subtitle
    // detection off, a record whose hash still matches was written under today's settings
    // and carries the media version. One with a stale hash may carry the sidecar version a
    // subtitle-detecting pass recorded, so its different version cannot tell a sidecar change
    // from a replaced file, and with no other record to confirm the media version the item is
    // reset all the same, whether or not the pass runs the mode.
    [Theory]
    [InlineData(AnalysisMode.Recap, true, true)]
    [InlineData(AnalysisMode.Recap, false, true)]
    [InlineData(AnalysisMode.Recap, true, false)]
    [InlineData(AnalysisMode.Recap, false, false)]
    [InlineData(AnalysisMode.Preview, true, true)]
    [InlineData(AnalysisMode.Preview, false, true)]
    [InlineData(AnalysisMode.Preview, true, false)]
    [InlineData(AnalysisMode.Preview, false, false)]
    public async Task VerifyQueueAsync_SubtitleModeRecordWithDetectionOff_FlagsChangedFileWithoutAConfirmingRecord(AnalysisMode mode, bool hashMatches, bool modeInPass)
    {
        var config = new PluginConfiguration
        {
            ScanIntroduction = !modeInPass,
            ScanCredits = false,
            ScanRecap = modeInPass && mode == AnalysisMode.Recap,
            ScanPreview = modeInPass && mode == AnalysisMode.Preview,
            ScanCommercial = false,
        };
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var database = fixture.CreateDatabase();
        await database.SetAnalyzerActionAsync(fixture.SeasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [mode] = AnalyzerAction.Chapter });
        var hash = hashMatches ? ConfigHasher.Analysis(config, mode, AnalyzerAction.Chapter, ffmpegValid: true) : "stale-hash";
        await database.MarkItemsAnalyzedAsync(mode, [(fixture.EpisodeId, (long?)1)], hash);

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: true, modeInPass ? [mode] : [AnalysisMode.Introduction]));

        Assert.True(episode.FileChanged);
        if (modeInPass)
        {
            Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(mode));
        }
    }

    // A sidecar version change reopens the mode under a matching hash and is logged at
    // Information level. When the hash is stale too, the hash change is the useful reason.
    // The Introduction record confirms the media version, so the file counts as unchanged.
    [Theory]
    [InlineData(true, "files changed")]
    [InlineData(false, "configuration hash changed")]
    public async Task LogAnalysisReasons_ModeReopenedByItsFileVersion_LogsTheReasonAtInformation(bool hashMatches, string reason)
    {
        var config = SubtitleRecapConfig();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), SeasonId = Guid.NewGuid(), SeasonNumber = 1, FileVersion = 2, SubtitleFileVersion = 5 };
        var hash = hashMatches ? ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, ffmpegValid: true) : "stale-hash";
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(episode.EpisodeId, (long?)2)], "introduction-hash");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Recap, [(episode.EpisodeId, (long?)3)], hash);
        var verifier = new QueueVerifier(config, [AnalysisMode.Recap], await database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]), ffmpegValid: true);
        var logger = new CapturingLogger<QueueVerifier>();

        verifier.Classify(episode);
        verifier.LogAnalysisReasons(logger, [episode]);

        Assert.False(episode.FileChanged);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(reason, entry.Message, StringComparison.Ordinal);
    }

    // Subtitle detection never reads a sidecar in an unselected language, so adding one
    // leaves the recorded version, and the mode, as they were.
    [Fact]
    public async Task VerifyQueueAsync_SidecarInAnUnselectedLanguage_KeepsTheModeSettled()
    {
        var config = SubtitleRecapConfig();
        config.SubtitleLanguages = "ger";
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        await fixture.SettleIntroductionAndRecapAsync();

        fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nPreviously on\n");

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: false, IntroductionAndRecap));
        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Recap));
    }

    // Jellyfin saves a downloaded subtitle to the item's internal metadata folder when the
    // library does not save subtitles with the media. Subtitle detection reads it there, so
    // adding one reopens the mode that reads it.
    [Fact]
    public async Task VerifyQueueAsync_SidecarAddedToTheMetadataFolder_ReopensTheSubtitleMode()
    {
        using var fixture = new VerifyQueueFixture(SubtitleRecapConfig(), fileVersion: 2);
        await fixture.SettleIntroductionAndRecapAsync();

        fixture.WriteSidecar(".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nPreviously on\n", inMetadataFolder: true);

        var episode = Assert.Single(await fixture.VerifyAsync(ffmpegValid: false, IntroductionAndRecap));
        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Recap));
    }

    [Fact]
    public async Task AnalyzeItemsAsync_ChangedFile_DiscardsSegmentsAndFingerprintsBeforeReanalysis()
    {
        var config = new PluginConfiguration
        {
            ScanIntroduction = true,
            ScanCredits = false,
            ScanRecap = false,
            ScanPreview = false,
            ScanCommercial = false,
        };
        using var fixture = new VerifyQueueFixture(config, fileVersion: 2);
        var hash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.None, ffmpegValid: false);
        var database = fixture.CreateDatabase();
        var cacheDbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);

        // A season whose analyzer action is None runs no analyzer, so what the pass does
        // before analysis is all that is left to observe.
        await database.SetAnalyzerActionAsync(fixture.SeasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.None });
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)1)], hash);
        await database.ReplaceAutoSegmentsAsync(fixture.EpisodeId, AnalysisMode.Introduction, [new Segment(fixture.EpisodeId, new TimeRange(0, 30))], SegmentSource.Chromaprint, hash);
        cacheDatabase.Upsert(fixture.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, string.Empty);

        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => false }, cacheDbPath);

        Assert.DoesNotContain(await database.GetSegmentsAsync(fixture.EpisodeId, includeSuppressed: true), s => s.State == SegmentState.Active);
        using var cache = DatabaseTestHelpers.CreateCacheContext(cacheDbPath);
        Assert.False(await cache.DetectionCache.AnyAsync());
        using var verifyDb = DatabaseTestHelpers.CreateSegmentContext(fixture.DbPath);
        var record = await verifyDb.AnalyzedItems.AsNoTracking().SingleAsync();
        Assert.Equal(hash, record.ConfigHash);
        Assert.Equal(2, record.FileVersion);
    }

    // Subtitle rows were timed against the replaced file, so the reset drops them with the
    // other automatic rows. Kept as the fallback, they would outlive any scan of the new
    // file that settles nothing, such as this incomplete one.
    [Fact]
    public async Task AnalyzeItemsAsync_ChangedFile_DiscardsSubtitleRows()
    {
        using var fixture = new VerifyQueueFixture(SubtitleRecapConfig(), fileVersion: 2);
        var database = fixture.CreateDatabase();
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(fixture.EpisodeId, (long?)1)], "intro-hash");
        await database.ReplaceAutoSegmentsAsync(fixture.EpisodeId, AnalysisMode.Recap, [new Segment(fixture.EpisodeId, new TimeRange(10, 60))], SegmentSource.Subtitle, "recap-hash");

        await fixture.AnalyzeAsync(new StubFFmpegService { VersionCheck = () => false, Subtitles = _ => new SubtitleScan([], Complete: false) });

        Assert.DoesNotContain(await database.GetSegmentsAsync(fixture.EpisodeId), s => s.Type == AnalysisMode.Recap);
    }

    [Fact]
    public async Task VerifyQueueAsync_SkipsAnEpisodeTheServerNoLongerKnows()
    {
        using var fixture = new VerifyQueueFixture(new PluginConfiguration());

        // Resolved from the fixture's library, then verified against a server that has
        // since dropped the episode. Analyzing it would write under a stale id.
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());

        Assert.Empty(await fixture.VerifyAsync(ffmpegValid: false));
    }

    [Fact]
    public async Task VerifyQueueAsync_StillAnalyzesDisabledItem()
    {
        using var fixture = new VerifyQueueFixture(new PluginConfiguration());

        using (var db = DatabaseTestHelpers.CreateSegmentContext(fixture.DbPath))
        {
            await db.Database.MigrateAsync();
            db.DisabledItems.Add(new DbDisabledItem(fixture.EpisodeId));
            await db.SaveChangesAsync();
        }

        var verified = await fixture.VerifyAsync(ffmpegValid: false);

        // The disable flag only withholds output from Jellyfin; the analysis
        // pipeline must keep processing the episode.
        Assert.Equal(fixture.EpisodeId, Assert.Single(verified).EpisodeId);
    }

    // Scans Introduction and Recap, with subtitle recap detection on.
    private static PluginConfiguration SubtitleRecapConfig() => new()
    {
        ScanIntroduction = true,
        ScanCredits = false,
        ScanRecap = true,
        ScanPreview = false,
        ScanCommercial = false,
        EnableSubtitleRecapDetection = true,
    };

    // Scans Preview, and Recap when asked, with subtitle preview detection on and chapter
    // previews taken as authored.
    private static PluginConfiguration PreviewOnlyConfig(bool scanRecap) => new()
    {
        ScanIntroduction = false,
        ScanCredits = false,
        ScanRecap = scanRecap,
        ScanPreview = true,
        ScanCommercial = false,
        ReanalyzeSettledSeasons = false,
        EnableSubtitlePreviewDetection = true,
        EnableSponsorBlockChapterDetection = false,
        SnapToKeyframe = false,
    };

    // A recap chapter from 10 to 100 s.
    private static ChapterInfo[] RecapChapters() =>
    [
        new() { Name = "Opening", StartPositionTicks = 0 },
        new() { Name = "Recap", StartPositionTicks = DatabaseTestHelpers.Ticks(10) },
        new() { Name = "Episode", StartPositionTicks = DatabaseTestHelpers.Ticks(100) },
    ];

    private static ChapterInfo[] PreviewChapters(double previewStart) =>
    [
        new() { Name = "Episode", StartPositionTicks = 0 },
        new() { Name = "Preview", StartPositionTicks = DatabaseTestHelpers.Ticks(previewStart) },
    ];

    // Scans Introduction only, from chapters, with SponsorBlock labels off.
    private static PluginConfiguration ChapterSourceConfig() => new()
    {
        ScanIntroduction = true,
        ScanCredits = false,
        ScanRecap = false,
        ScanPreview = false,
        ScanCommercial = false,
        ReanalyzeSettledSeasons = false,
        EnableSponsorBlockChapterDetection = false,
    };

    // No Chromaprint, the file's own chapters, and time adjustment probes that find nothing,
    // so a chapter range is stored as authored.
    private static StubFFmpegService ChapterSourceFfmpeg(ChapterInfo[] embeddedChapters) => new()
    {
        VersionCheck = () => false,
        EmbeddedChapters = _ => embeddedChapters,
        Silence = (_, _, _) => [],
        KeyFrames = (_, _, _) => [],
    };

    private static ChapterInfo Chapter(string name, double start)
        => new() { Name = name, StartPositionTicks = DatabaseTestHelpers.Ticks(start) };

    private static ChapterInfo[] IntroChapters(double start, double end)
        => [Chapter("Episode", 0), Chapter("Opening", start), Chapter("Story", end)];

    private static async Task<(double Start, double End)> ActiveRangeAsync(IntroSkipperDatabase database, Guid itemId, AnalysisMode mode)
    {
        var row = Assert.Single(await database.GetSegmentsAsync(itemId), row => row.Type == mode && row.State == SegmentState.Active);
        return (TickConversions.ToSeconds(row.StartTicks), TickConversions.ToSeconds(row.EndTicks));
    }

    // The production eligibility decision: the same batch read (GetSettleReanalysisStatesAsync)
    // and SeasonReanalysisPlanner.GetSettleReanalysisModes call the analyzer makes.
    private static async Task<bool> ShouldReanalyzeAsync(
        IntroSkipperDatabase database,
        Guid seasonId,
        AnalysisMode mode,
        IReadOnlyCollection<Guid> episodeIds)
    {
        var states = await database.GetSettleReanalysisStatesAsync(seasonId);
        return SeasonReanalysisPlanner.GetSettleReanalysisModes(states, episodeIds, [mode], ffmpegValid: true).Count > 0;
    }

    /// <summary>
    /// One queued episode, four minutes long, whose media file exists on disk, the only item
    /// of one library in the plugin instance's library manager, over a fresh segment
    /// database. The file version is the write time Jellyfin holds for the episode, in ticks.
    /// The episode has the given chapters, or none.
    /// </summary>
    private sealed class VerifyQueueFixture : IDisposable
    {
        private readonly TempSegmentDb _db = new();
        private readonly EntrypointTestHelpers.PluginInstanceScope _scope;
        private readonly string _mediaPath;
        private readonly Episode _episode;
        private readonly List<string> _sidecars = [];

        public VerifyQueueFixture(PluginConfiguration config, long fileVersion = 0, ChapterInfo[]? chapters = null)
        {
            _mediaPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
            File.WriteAllText(_mediaPath, string.Empty);

            _scope = EntrypointTestHelpers.CreatePluginScope(config, chapters ?? []);
            _episode = JellyfinItems.Episode(EpisodeId, Guid.NewGuid(), SeasonId, path: _mediaPath);
            _episode.DateModified = new DateTime(fileVersion, DateTimeKind.Utc);
            LibraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents(_episode));
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", LibraryManager);
        }

        public string DbPath => _db.Path;

        public ILibraryManager LibraryManager { get; }

        public Guid SeasonId { get; } = Guid.NewGuid();

        public Guid EpisodeId { get; } = Guid.NewGuid();

        public IntroSkipperDatabase CreateDatabase() => _db.CreateDatabase();

        // Writes a subtitle sidecar named like the media file, e.g. suffix ".en.srt", next to
        // the media file or in the item's internal metadata folder, and returns its path. The
        // fixture deletes it.
        public string WriteSidecar(string suffix, string text, bool inMetadataFolder = false)
        {
            var path = inMetadataFolder
                ? Path.Join(Directory.CreateDirectory(_episode.GetInternalMetadataPath()).FullName, Path.GetFileNameWithoutExtension(_mediaPath) + suffix)
                : Path.ChangeExtension(_mediaPath, suffix);
            File.WriteAllText(path, text);
            _sidecars.Add(path);
            return path;
        }

        // Records Introduction and Recap as analyzed through a full pass without running an
        // analyzer: a season whose analyzer action is None records each mode with the file
        // version the mode depends on, under the hash for the given Chromaprint availability.
        public async Task SettleIntroductionAndRecapAsync(bool chromaprintAvailable = false)
        {
            await _db.CreateDatabase().SetAnalyzerActionAsync(
                SeasonId,
                new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.None, [AnalysisMode.Recap] = AnalyzerAction.None });
            await AnalyzeAsync(new StubFFmpegService { VersionCheck = () => chromaprintAvailable });
        }

        // Runs a full pass over the season, as the scheduler does, with a fresh detection
        // cache unless the test passes the path of one it seeded.
        public Task AnalyzeAsync(IFFmpegService ffmpeg, string? cacheDbPath = null)
        {
            cacheDbPath ??= DatabaseTestHelpers.CreateTempCacheDbPath();
            return new BaseItemAnalyzerTask(
                NullLoggerFactory.Instance,
                EntrypointTestHelpers.CreateSeasonResolver(LibraryManager),
                ffmpeg,
                DatabaseTestHelpers.CreateCacheService(cacheDbPath),
                DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath),
                _db.CreateDatabase()).AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None, [SeasonId]);
        }

        // Verifies the season as a pass would: resolved from the library right before
        // verification, so the path and file version come from the resolver. Without
        // modes, the pass covers Introduction only.
        public Task<IReadOnlyList<QueuedEpisode>> VerifyAsync(bool ffmpegValid, IReadOnlyCollection<AnalysisMode>? modes = null)
        {
            var resolver = EntrypointTestHelpers.CreateSeasonResolver(LibraryManager);
            var task = new BaseItemAnalyzerTask(
                NullLoggerFactory.Instance,
                resolver,
                new StubFFmpegService { VersionCheck = () => ffmpegValid },
                cacheService: null!,
                cacheDatabase: null!,
                _db.CreateDatabase());
            return task.VerifyQueueAsync(resolver.ResolveDisplayed(SeasonId)!.Episodes, modes ?? [AnalysisMode.Introduction], ffmpegValid);
        }

        public void Dispose()
        {
            _scope.Dispose();
            File.Delete(_mediaPath);
            _sidecars.ForEach(File.Delete);
            if (Directory.Exists(_episode.GetInternalMetadataPath()))
            {
                Directory.Delete(_episode.GetInternalMetadataPath());
            }
            _db.Dispose();
        }
    }
}
