// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static IntroSkipper.Tests.BlackFrameFixtures;
using static IntroSkipper.Tests.StubFFmpegService;

/// <summary>
/// The credits pass over the stub ffmpeg: two 1000 second episodes whose credits window is
/// the last 450 seconds. Each episode's keyframe scan has a keyframe every 2 seconds across the
/// window. By default the pages from 950 to the end are a black roll, and each episode the keyframe
/// analyzer runs on issues one boundary probe over the gap from the keyframe at 948. The shared
/// audio, when a test enables it, runs from about 748 to 983.
/// </summary>
public sealed class TestCreditsPass
{
    private const double Duration = 1000;
    private const double WindowStart = Duration - 450;
    private const double BlackStart = 950;
    private const int SharedAudioFirstPoint = 1600;
    private const int DefaultSharedAudioLastPoint = 3499;

    // The boundary probe of the default roll, over the gap from the keyframe before it.
    private static readonly RangeScan RollProbe = new(948, 950, 95, 28, AnalysisMode.Credits);

    [Theory]
    [InlineData(AnalyzerAction.Default, false)]
    [InlineData(AnalyzerAction.Default, true)]
    [InlineData(AnalyzerAction.Chapter, true)]
    [InlineData(AnalyzerAction.Chromaprint, false)]
    public async Task RecognizedCreditsChapter_IsAuthoritativeByDefault(AnalyzerAction action, bool ffmpegValid)
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();
        var config = new PluginConfiguration { PreferChromaprint = true };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, action, ffmpegValid, CancellationToken.None);

        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
        Assert.Equal(0, ffmpeg.VisualScanCalls);
        foreach (var episode in episodes)
        {
            var row = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
            Assert.Equal(SegmentSource.Chapter, row.Source);
            Assert.Equal((900, 950), (row.ToSegment().Start, row.ToSegment().End));
            Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits));
        }
    }

    [Theory]
    [InlineData("Chapter 02", 900)]
    [InlineData("Ending", 995)]
    public async Task UnmatchedOrInvalidChapter_StillUsesCombinedDetection(string name, double start)
    {
        using var scope = Scope(Chapter("Main", 0), Chapter(name, start));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(2, ffmpeg.FingerprintCalls);
        Assert.Equal(2, ffmpeg.CreditsScanCalls);
        Assert.Equal(2, ffmpeg.VisualScanCalls);
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);
    }

    [Fact]
    public async Task AuthoritativeChapter_RespectsFullLengthChaptersBelowTheCreditsMinimum()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 970), Chapter("Preview", 975));
        var (episodes, ffmpeg, database) = CreateSeason();
        var config = new PluginConfiguration { FullLengthChapters = true };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chapter, row.Source);
        Assert.Equal((970, 975), (row.ToSegment().Start, row.ToSegment().End));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(0, 60)]
    public async Task AuthoritativeChapter_ConsumedByOffsets_ClearsStaleCreditsWithoutFallback(int startOffset, int endOffset)
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950));
        var (episodes, ffmpeg, database) = CreateSeason();
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(900, 950))], SegmentSource.Chapter);
        var config = new PluginConfiguration { IntroStartOffset = startOffset, IntroEndOffset = endOffset };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.All(episodes, episode => Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Credits)));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedSeason_ChapterResultSuppliesComparisonWithoutBeingReplaced(bool alreadyAnalyzed)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", ChapterManagerStub.CreateForItems(
            id => id == episodes[0].EpisodeId ? [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950)] : [], out _));
        Guid? storedId = null;
        if (alreadyAnalyzed)
        {
            await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(900, 950))], SegmentSource.Chapter);
            episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
            storedId = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Id;
            await CacheFingerprintsAsync(cache, ffmpeg, [episodes[0]]);
        }

        await CreatePass(ffmpeg, database, cache, new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(1, ffmpeg.CreditsScanCalls);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
        Assert.Equal([RollProbe], ffmpeg.Calls);
        var chapter = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chapter, chapter.Source);
        Assert.Equal((900, 950), (chapter.ToSegment().Start, chapter.ToSegment().End));
        if (storedId.HasValue)
        {
            Assert.Equal(storedId.Value, chapter.Id);
        }

        var combined = Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId));
        Assert.Equal(SegmentSource.Combined, combined.Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), combined.ToSegment().Start, 0.5);
        Assert.Equal(Duration, combined.ToSegment().End);
    }

    [Theory]
    [InlineData(AnalyzerAction.BlackFrame, SegmentSource.BlackFrame)]
    [InlineData(AnalyzerAction.Chromaprint, SegmentSource.Chromaprint)]
    public async Task ExplicitAnalyzerOverride_IgnoresAuthoritativeChapters(AnalyzerAction action, SegmentSource source)
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, action, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(source, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);

        // Only the black-frame action runs the keyframe analyzer.
        Call[] probes = action == AnalyzerAction.BlackFrame ? [RollProbe, RollProbe] : [];
        Assert.Equal(probes, ffmpeg.Calls);
    }

    [Fact]
    public async Task AuthoritativeChapters_DoNotReplaceUserProvidedCredits()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();
        await database.SeedUserSegmentAsync(episodes[0].EpisodeId, AnalysisMode.Credits, TimeSpan.FromSeconds(700).Ticks, TimeSpan.FromSeconds(720).Ticks);
        episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.UserProvided);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.User, row.Source);
        Assert.Equal((700, 720), (row.ToSegment().Start, row.ToSegment().End));
        Assert.Equal(EpisodeState.UserProvided, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
    }

    [Fact]
    public async Task CancellationBeforeChapterAnalysis_RethrowsWithoutPersistence()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900));
        var (episodes, ffmpeg, database) = CreateSeason();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreatePass(ffmpeg, database, config: new PluginConfiguration())
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, cancellation.Token));

        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
    }

    [Fact]
    public async Task OverlappingBlackFrameAndChromaprint_WriteOneCombinedSegment()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(WindowStart, ffmpeg.LastCreditsScanStart);
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
        foreach (var episode in episodes)
        {
            var segment = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
            Assert.Equal(SegmentSource.Combined, segment.Source);
            Assert.Equal(PointTime(SharedAudioFirstPoint), segment.ToSegment().Start, 0.5);
            Assert.Equal(Duration, segment.ToSegment().End);
            Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits));
        }
    }

    [Theory]
    [InlineData(DefaultSharedAudioLastPoint)]
    [InlineData(3560)]
    public async Task BlackRollStartingBeforeTheSharedAudio_IsCombinedWithIt(int sharedAudioLastPoint)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(700, Duration, 95, KeyframeVisuals.Black)], sharedAudioLastPoint: sharedAudioLastPoint);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Combined, segment.Source);
        Assert.Equal(700, segment.ToSegment().Start);
        Assert.Equal(Duration, segment.ToSegment().End);
        Assert.Equal(
            [new RangeScan(698, 700, 95, 28, AnalysisMode.Credits), new RangeScan(698, 700, 95, 28, AnalysisMode.Credits)],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task BlackRollBetweenAChapterAndTheSharedAudio_IsCombinedWithTheAudio()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660));
        var (episodes, ffmpeg, database) = CreateSeason([(700, Duration, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(600, 660, SegmentSource.Chapter), (700, Duration, SegmentSource.Combined)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [new RangeScan(698, 700, 95, 28, AnalysisMode.Credits), new RangeScan(698, 700, 95, 28, AnalysisMode.Credits)],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task UnrelatedChapterAfterTheCredits_DoesNotCapTheTrailingRoll()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 700), Chapter("Epilogue", 760), Chapter("Chapter 04", 950));
        var (episodes, ffmpeg, database) = CreateSeason([(900, Duration, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(700, 760, SegmentSource.Chapter), (900, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [new RangeScan(898, 900, 95, 28, AnalysisMode.Credits), new RangeScan(898, 900, 95, 28, AnalysisMode.Credits)],
            ffmpeg.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChapteredPreviewAfterTheCredits_IsNeitherExtendedNorMergedInto(bool enhanceChapters)
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 980));
        var (episodes, ffmpeg, database) = CreateSeason();
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Preview, [new Segment(episodes[0].EpisodeId, new TimeRange(980, Duration))], SegmentSource.CreditsDerived);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = enhanceChapters })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);
        await AnimePreviewDeriver.DeriveAsync(database, episodes, 15, CancellationToken.None);

        var rows = await database.GetSegmentsAsync(episodes[0].EpisodeId);
        var credits = Assert.Single(rows, s => s.Type == AnalysisMode.Credits).ToSegment();
        Assert.Equal((900, 980), (credits.Start, credits.End));
        var preview = Assert.Single(rows, s => s.Type == AnalysisMode.Preview).ToSegment();
        Assert.Equal((980, Duration), (preview.Start, preview.End));

        // Without enhancement the credits chapter settles both episodes before any scan.
        Call[] probes = enhanceChapters ? [RollProbe, RollProbe] : [];
        Assert.Equal(probes, ffmpeg.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChromaprintOnlyFailure_LeavesTheEpisodesRetriable(bool seasonWide)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            fingerprintFailure: _ => seasonWide ? new TimeoutException("ffmpeg hung") : new FingerprintException("no audio"));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Chromaprint, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Credits));
            Assert.Empty(await database.GetSegmentsAsync(episode.EpisodeId));
        }
    }

    [Fact]
    public async Task ChromaprintWithinMinimumDurationOfTheEnd_IsExtendedToIt()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([], sharedAudioLastPoint: 3560); // shared audio to about 991 of 1000

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chromaprint, segment.Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), segment.ToSegment().Start, 0.5);
        Assert.Equal(Duration, segment.ToSegment().End);
    }

    [Theory]
    [InlineData(700.0, 720.0, 698.0)]
    [InlineData(650.0, 700.0, 648.0)]
    public async Task BlackCreditsSeparatedFromTheSharedAudio_AreKept(double blackStart, double blackEnd, double keyframeBefore)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([(blackStart, blackEnd, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal((blackStart, blackEnd, SegmentSource.BlackFrame), (segments[0].ToSegment().Start, segments[0].ToSegment().End, segments[0].Source));
        Assert.Equal(SegmentSource.Chromaprint, segments[1].Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), segments[1].ToSegment().Start, 0.5);
        Assert.Equal(PointTime(DefaultSharedAudioLastPoint), segments[1].ToSegment().End, 0.5);
        Assert.Equal(
            [new RangeScan(keyframeBefore, blackStart, 95, 28, AnalysisMode.Credits), new RangeScan(keyframeBefore, blackStart, 95, 28, AnalysisMode.Credits)],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task BlackFrameAction_RestrictsThePassToBlackFrameCandidates()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(0, ffmpeg.FingerprintCalls);
        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.BlackFrame, segment.Source);
        Assert.Equal(BlackStart, segment.ToSegment().Start);
        Assert.Equal(Duration, segment.ToSegment().End);
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task ChapterAction_DoesNotRestrictThePass()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Chapter, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.BlackFrame, segment.Source);
        Assert.Equal(BlackStart, segment.ToSegment().Start);
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task BlackFrameAfterTheChapterFollowingTheCredits_StaysItsOwnSegment()
    {
        // CITY THE ANIMATION E08: the dubbing cards sit inside the "epilogue" chapter that
        // follows the ending chapter, so the authored boundary keeps them apart.
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(900, 950, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task CandidatesSeparatedByContent_AreWrittenApartUnderTheirOwnSources()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(600, 660, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task UserProvidedNeighbour_IsOnlyUsedForComparison()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        await database.SeedUserSegmentAsync(episodes[1].EpisodeId, AnalysisMode.Credits, TimeSpan.FromSeconds(700).Ticks, TimeSpan.FromSeconds(720).Ticks);
        episodes[1].SetAnalyzed(AnalysisMode.Credits, EpisodeState.UserProvided);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);
        Assert.Equal(SegmentSource.User, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
        Assert.Equal(EpisodeState.UserProvided, episodes[1].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal([RollProbe], ffmpeg.Calls);
    }

    [Fact]
    public async Task FingerprintFailure_WritesTheOtherCandidatesAndSettlesTheEpisode()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(fingerprintFailure: episode => episode.EpisodeNumber == 1 ? new FingerprintException("no audio") : null);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(SegmentSource.BlackFrame, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);
        Assert.All(episodes, episode => Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits)));
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task ComparisonFailure_WritesTheOtherCandidatesAndSettlesEveryEpisode()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(fingerprintFailure: _ => new InvalidOperationException("pipe closed"));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            Assert.Equal(SegmentSource.BlackFrame, Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId)).Source);
            Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits));
        }

        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task RequeuedSibling_WhoseCandidateIsAlreadyStored_IsNotRescanned()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);
        var storedIds = new List<Guid>();
        foreach (var episode in episodes)
        {
            storedIds.Add(Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId)).Id);
        }

        await CacheFingerprintsAsync(cache, ffmpeg, episodes);
        episodes.Add(Episode(episodes[0].SeasonId, 3));
        var scansBefore = ffmpeg.CreditsScanCalls;

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(scansBefore + 1, ffmpeg.CreditsScanCalls);
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[2].EpisodeId)).Source);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(storedIds[i], Assert.Single(await database.GetSegmentsAsync(episodes[i].EpisodeId)).Id);
        }

        // Both runs: two episodes in the first, only the new one in the second.
        Assert.Equal(
            [RollProbe, RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task RequeuedSibling_WhoseCandidateReachesOutsideStoredCredits_IsRecombined()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        // Stored credits shrink to the roll, as a season analyzed under the chain would have.
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(BlackStart, Duration))], SegmentSource.BlackFrame);
        await CacheFingerprintsAsync(cache, ffmpeg, episodes);
        episodes.Add(Episode(episodes[0].SeasonId, 3));

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Combined, segment.Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), segment.ToSegment().Start, 0.5);

        // Both runs: two episodes in the first; in the second the recombined episode and the new one.
        Assert.Equal(
            [RollProbe, RollProbe, RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task BlackFrameScanFailure_FailsThatEpisodeAndContinues()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(scanFailure: episode => episode.EpisodeNumber == 1 ? new InvalidOperationException("scan failed") : null);
        var failing = episodes[0].EpisodeId;

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.True(episodes[0].NeedsAnalysis(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(failing));
        Assert.Equal(SegmentSource.BlackFrame, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
        Assert.Equal([RollProbe], ffmpeg.Calls);
    }

    [Fact]
    public async Task CardCreditsBeforeTheBlackRoll_AreCombinedWithIt()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        foreach (var episode in episodes)
        {
            var segment = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
            Assert.Equal((BlackStart - 60, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        }

        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task CardCreditsAlone_AreStoredUnderTheirOwnSource()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([(Duration - 60, Duration, 0, KeyframeVisuals.Card)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((Duration - 60, Duration, SegmentSource.KeyframeVisuals), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
    }

    [Fact]
    public async Task CardCreditsSeparatedFromTheBlackRoll_AreStoredApart()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(800, 830, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(800, 830, SegmentSource.KeyframeVisuals), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task ShortCardCreditsBeforeAShortBlackRoll_QualifyTogether()
    {
        // The 12 s roll is short of the minimum even with the keyframe gap before it, so the analyzer
        // asks blackdetect, which confirms nothing. With no accepted scene the card-like roll pages
        // count as cards.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(976, 986, 0, KeyframeVisuals.Card), (988, Duration, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((976, Duration, SegmentSource.KeyframeVisuals), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        Assert.Equal(
            [new IntervalScan(973, 1000, 28, 85), new IntervalScan(973, 1000, 28, 85)],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task DarkLeadInBeforeTheBlackRoll_StaysOutOfTheCardCandidate()
    {
        // 900 to 940 is 90 percent black and a black page to the visuals, 942 to 978 is the roll, cards
        // follow to the end. The black-frame transition check starts the roll at 942; the card candidate
        // must not reach back into the lead-in. The boundary probe reads the gap from the lead-in's last
        // keyframe.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(900, 940, 90, KeyframeVisuals.Black), (942, 978, 100, KeyframeVisuals.Black), (980, Duration, 0, KeyframeVisuals.Card)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((942, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        Assert.Equal(
            [new RangeScan(940, 942, 95, 28, AnalysisMode.Credits), new RangeScan(940, 942, 95, 28, AnalysisMode.Credits)],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task MixedCardRunBeforeASeparateBlackRoll_IsStoredBesideIt()
    {
        // Cards 560 to 570, black cards 572 to 590, content, then a roll from 950 to the end. The
        // black-frame rules accept both black scenes and each is a black-frame candidate. The earlier
        // scene lets the mixed run qualify as a card run and lies inside it, so the two merge into one
        // segment beside the roll. Each scene gets a boundary probe from the keyframe before it.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(560, 570, 0, KeyframeVisuals.Card), (572, 590, 100, KeyframeVisuals.Black), (BlackStart, Duration, 100, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(560, 590, SegmentSource.Combined), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [
                new RangeScan(570, 572, 95, 28, AnalysisMode.Credits),
                RollProbe,
                new RangeScan(570, 572, 95, 28, AnalysisMode.Credits),
                RollProbe,
            ],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task CreditsSplitByAMidCreditsScene_AreStoredAsTwoSegments()
    {
        // Daredevil: Born Again S01E09 has 28 s of single credit cards on black, an 80 s dark
        // mid-credits scene, then the rest of the roll to the end. The black-frame rules accept both
        // parts and each is a candidate, so the scene between them plays. Every page of the parts is
        // a black card, so the card run finder adds nothing.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(800, 828, 100, KeyframeVisuals.Black), (830, 906, 60, KeyframeVisuals.Dark), (908, Duration, 100, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(800, 828, SegmentSource.BlackFrame), (908, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [
                new RangeScan(798, 800, 95, 28, AnalysisMode.Credits),
                new RangeScan(906, 908, 95, 28, AnalysisMode.Credits),
                new RangeScan(798, 800, 95, 28, AnalysisMode.Credits),
                new RangeScan(906, 908, 95, 28, AnalysisMode.Credits),
            ],
            ffmpeg.Calls);
    }

    [Theory]
    [InlineData(800.0)]
    [InlineData(BlackStart)]
    public async Task BoundaryProbeFailureOnAnyAcceptedScene_FailsTheEpisode(double failingSceneStart)
    {
        // A roll from 950 sits beside an accepted scene from 800 to 830, and both get the boundary
        // probe. A probe that throws fails the episode whichever scene it belongs to, so nothing is
        // written and the next scan retries.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(800, 830, 100, KeyframeVisuals.Black), (BlackStart, Duration, 100, KeyframeVisuals.Black)],
            rangeScan: (_, range, _, _, _) => range.End == failingSceneStart ? throw new InvalidOperationException("probe failed") : []);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));

        // Scenes are probed in time order, and the probe that throws is each episode's last.
        Call[] episodeProbes = failingSceneStart == 800
            ? [new RangeScan(798, 800, 95, 28, AnalysisMode.Credits)]
            : [new RangeScan(798, 800, 95, 28, AnalysisMode.Credits), RollProbe];
        Assert.Equal([.. episodeProbes, .. episodeProbes], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectNonBlackCreditsOff_IgnoresCardCredits()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.Black)]);
        var config = new PluginConfiguration { DetectNonBlackCredits = false };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((BlackStart, Duration, SegmentSource.BlackFrame), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task BlackFrameAction_KeepsTheCardCandidate()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.Black)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((BlackStart - 60, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task LegacyBlackFrameAnalyzer_FeedsThePassUnderItsToggle()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.Black)],
            rangeScan: (_, range, _, _, _) => range.Start is >= BlackStart and < Duration ? [new BlackFrame(95, 0, 0)] : []);
        var config = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, UseChapterMarkersBlackFrame = false, EnhanceChapterCredits = true };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(0, ffmpeg.VisualScanCalls);
        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal([SegmentSource.Chapter, SegmentSource.BlackFrame], segments.Select(s => s.Source).ToList());
        Assert.Equal((900, 950), (segments[0].ToSegment().Start, segments[0].ToSegment().End));
        Assert.InRange(segments[1].ToSegment().Start, BlackStart, BlackStart + 10); // legacy binary search precision
        Assert.Equal(Duration, segments[1].ToSegment().End);

        // The legacy binary search, one analyzer per season: the second episode starts from where
        // the first one's search settled.
        Assert.Equal(
        [
            Probe(954, 955),
            Probe(970, 972),
            Probe(962.5, 964.5),
            Probe(958.75, 960.75),
            Probe(949.375, 951.375),
            Probe(955.0625, 957.0625),
            Probe(955.0625, 957.0625),
            Probe(947.5625, 949.5625),
            Probe(952.3125, 954.3125),
        ], ffmpeg.Calls);

        static RangeScan Probe(double start, double end) => new(start, end, 85, 28, AnalysisMode.Credits);
    }

    [Fact]
    public async Task NoCandidates_SettlesTheEpisodeWithoutSegments()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(EpisodeState.NoSegments, episodes[0].GetAnalyzed(AnalysisMode.Credits));
    }

    [Theory]
    [InlineData(AnalyzerAction.BlackFrame, true)]
    [InlineData(AnalyzerAction.Default, false)]
    [InlineData(AnalyzerAction.Default, true)]
    [InlineData(AnalyzerAction.Chromaprint, true)]
    public async Task PreviouslyFailedEpisodes_NoCandidates_ClearStaleSegmentsAndSettle(AnalyzerAction action, bool ffmpegValid)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([], sharedAudioLastPoint: SharedAudioFirstPoint - 1);
        foreach (var episode in episodes)
        {
            await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits, [new Segment(episode.EpisodeId, new TimeRange(BlackStart, Duration))], SegmentSource.BlackFrame);
            episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.AnalysisFailed);
        }

        await CreatePass(ffmpeg, database).RunAsync(episodes, action, ffmpegValid, CancellationToken.None);

        foreach (var episode in episodes)
        {
            Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Credits));
            Assert.Empty(await database.GetSegmentsAsync(episode.EpisodeId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneNewChapteredEpisode_DoesNotReadSettledSiblingsChapters(bool ffmpegValid)
    {
        using var scope = Scope();
        var (_, ffmpeg, database) = CreateSeason();
        var seasonId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 26).Select(number => Episode(seasonId, number)).ToList();
        foreach (var episode in episodes.Take(25))
        {
            episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        }

        var manager = ChapterManagerStub.Create([Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)], out var chapters);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", manager);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid, CancellationToken.None);

        Assert.Equal(1, chapters.GetChaptersCallCount);
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
        Assert.Equal(SegmentSource.Chapter, Assert.Single(await database.GetSegmentsAsync(episodes[25].EpisodeId)).Source);
    }

    [Fact]
    public async Task CoveredSettledSibling_DoesNotReadChapters()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits,
            [new Segment(episodes[0].EpisodeId, new TimeRange(700, Duration))], SegmentSource.Combined);
        episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        await CacheFingerprintsAsync(cache, ffmpeg, [episodes[0]]);
        var storedId = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Id;
        var chapterReads = new List<Guid>();
        var manager = ChapterManagerStub.CreateForItems(id =>
        {
            chapterReads.Add(id);
            return [];
        }, out _);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", manager);

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.NotEmpty(chapterReads);
        Assert.All(chapterReads, id => Assert.Equal(episodes[1].EpisodeId, id));
        Assert.Equal(1, ffmpeg.CreditsScanCalls);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
        Assert.Equal([RollProbe], ffmpeg.Calls);
        Assert.Equal(storedId, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Id);
        Assert.All(episodes, episode => Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits)));
    }

    /// <summary>
    /// A two-episode season where the chaptered episode's result is unusable: its audio must
    /// still serve as the reference, or the unchaptered sibling has nothing to compare against
    /// and would be recorded as having no credits.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnusableChapterResult_StillSuppliesComparisonWithoutBeingReplaced(bool chapterLookupFails)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var manager = ChapterManagerStub.CreateForItems(id => id != episodes[0].EpisodeId
            ? []
            : chapterLookupFails
                ? throw new InvalidOperationException("Chapter lookup failed")
                : [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 920)], out _);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", manager);
        var config = new PluginConfiguration { IntroEndOffset = 30 };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(chapterLookupFails ? EpisodeState.AnalysisFailed : EpisodeState.NoSegments, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(2, ffmpeg.FingerprintCalls);
        Assert.Equal(1, ffmpeg.CreditsScanCalls);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
        Assert.Equal([RollProbe], ffmpeg.Calls);
        Assert.Equal(EpisodeState.Analyzed, episodes[1].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LegacyBlackFrameFallback_AnalyzesOnlyTheUnchapteredEpisode(int chapteredIndex)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(rangeScan: (episode, range, _, _, _) => episode.EpisodeNumber == chapteredIndex + 1
            ? throw new InvalidOperationException("Authoritative chapter should not be scanned")
            : range.Start is >= BlackStart and < Duration ? [new BlackFrame(95, 0, 0)] : []);
        var manager = ChapterManagerStub.CreateForItems(id => id == episodes[chapteredIndex].EpisodeId
            ? [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950)]
            : [], out _);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", manager);
        var config = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, UseChapterMarkersBlackFrame = false };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var chapter = Assert.Single(await database.GetSegmentsAsync(episodes[chapteredIndex].EpisodeId));
        Assert.Equal(SegmentSource.Chapter, chapter.Source);
        Assert.Equal((900, 950), (chapter.ToSegment().Start, chapter.ToSegment().End));
        var fallback = Assert.Single(await database.GetSegmentsAsync(episodes[1 - chapteredIndex].EpisodeId));
        Assert.Equal(SegmentSource.BlackFrame, fallback.Source);
        Assert.InRange(fallback.ToSegment().Start, BlackStart, BlackStart + 10);
        Assert.Equal(Duration, fallback.ToSegment().End);

        // Only the unchaptered episode is searched: a probe of the chaptered one would be logged
        // before its hook throws.
        Assert.Equal(
        [
            Probe(954, 955),
            Probe(970, 972),
            Probe(962.5, 964.5),
            Probe(958.75, 960.75),
            Probe(949.375, 951.375),
            Probe(955.0625, 957.0625),
        ], ffmpeg.Calls);

        static RangeScan Probe(double start, double end) => new(start, end, 85, 28, AnalysisMode.Credits);
    }

    [Fact]
    public async Task Upgrade_PreservesExistingCombinedCreditsUntilReanalysis()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();
        var config = new PluginConfiguration();
        const string oldHash = "353008E48A5F559E";
        Assert.Equal(oldHash, ConfigHasher.Analysis(config, AnalysisMode.Credits, AnalyzerAction.Default, true));
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits,
            [new Segment(episodes[0].EpisodeId, new TimeRange(750, Duration))], SegmentSource.Combined, oldHash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [episodes[0].EpisodeId], oldHash);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(episodes[0].SeasonId, episodes.Select(e => e.EpisodeId).ToArray());
        var verifier = new QueueVerifier(config, [AnalysisMode.Credits], snapshot, true);
        foreach (var episode in episodes)
        {
            verifier.Classify(episode);
            episode.AnalysisConfigHash = oldHash;
        }

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var oldRow = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Combined, oldRow.Source);
        Assert.Equal((750, Duration), (oldRow.ToSegment().Start, oldRow.ToSegment().End));
        var newRow = Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId));
        Assert.Equal(SegmentSource.Chapter, newRow.Source);
        Assert.Equal((900, 950), (newRow.ToSegment().Start, newRow.ToSegment().End));
        Assert.Equal(oldRow.ConfigHash, newRow.ConfigHash);
    }

    private static double PointTime(int point) => WindowStart + (point * ChromaprintConstants.SampleDuration);

    private static IDisposable Scope(params ChapterInfo[] chapters)
        => EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration(), chapters);

    private static ChapterInfo Chapter(string name, double start)
        => new() { Name = name, StartPositionTicks = TimeSpan.FromSeconds(start).Ticks };

    private static CreditsPass CreatePass(StubFFmpegService ffmpeg, IIntroSkipperDatabase database, DetectionCacheService? cache = null, PluginConfiguration? config = null)
        => new(NullLoggerFactory.Instance, ffmpeg, cache ?? DatabaseTestHelpers.CreateTempCacheService(), database, config ?? new PluginConfiguration());

    /// <summary>
    /// A two-episode season over the stub. Each episode's keyframe scan comes from one keyframe list
    /// (see <see cref="ScanOf"/>). When <paramref name="spans"/> is null the pages from 950 to the end
    /// are a 95 percent black roll with lettering; an empty list gives busy content only. Range probes
    /// find nothing unless <paramref name="rangeScan"/> says otherwise and blackdetect finds no
    /// intervals, so neither probe throws for want of a hook; a lead-in decode, which no fixture here
    /// reaches, would. Silence and keyframe lookups return nothing so end adjustment leaves ends alone.
    /// </summary>
    private static (List<QueuedEpisode> Episodes, StubFFmpegService Ffmpeg, IIntroSkipperDatabase Database) CreateSeason(
        (double From, double To, int Percentage, Func<double, KeyframeVisual?> Visual)[]? spans = null,
        int sharedAudioLastPoint = DefaultSharedAudioLastPoint,
        Func<QueuedEpisode, Exception?>? fingerprintFailure = null,
        Func<QueuedEpisode, Exception?>? scanFailure = null,
        Func<QueuedEpisode, TimeRange, int, int, AnalysisMode, BlackFrame[]>? rangeScan = null)
    {
        var seasonId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 2).Select(number => Episode(seasonId, number)).ToList();
        var scanSpans = spans ?? [(BlackStart, Duration, 95, KeyframeVisuals.Black)];

        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => fingerprintFailure?.Invoke(episode) is { } failure
                ? throw failure
                : SharedAudioFingerprint(episode, sharedAudioLastPoint),
            CreditsBlackFrames = (episode, _) => scanFailure?.Invoke(episode) is { } failure
                ? throw failure
                : ScanOf(episode, scanSpans).Rows,
            KeyframeVisuals = episode => ScanOf(episode, scanSpans).Visuals,
            RangeBlackFrames = rangeScan ?? ((_, _, _, _, _) => []),
            BlackIntervals = (_, _, _, _) => [],
            Silence = (_, _, _) => [],
            KeyFrames = (_, _, _) => [],
        };

        return (episodes, ffmpeg, DatabaseTestHelpers.CreateTempSegmentDatabase());
    }

    private static QueuedEpisode Episode(Guid seasonId, int number) => new()
    {
        EpisodeId = Guid.NewGuid(),
        SeasonId = seasonId,
        SeriesId = seasonId,
        EpisodeNumber = number,
        Name = $"Episode {number}",
        Path = $"/media/episode-{number}.mkv",
        Duration = Duration,
        CreditsFingerprintStart = WindowStart,
        CreditsFingerprintEnd = Duration,
    };

    /// <summary>
    /// One episode's keyframe scan: a keyframe every two seconds across the credits window, with its
    /// black row and its visual, relative to the credits window start like the scan reports them. A
    /// keyframe inside one of the <paramref name="spans"/>, given in file time, takes that span's
    /// black percentage and visual; any other keyframe is busy content, not black.
    /// </summary>
    private static (BlackFrame[] Rows, KeyframeVisual[] Visuals) ScanOf(QueuedEpisode episode, (double From, double To, int Percentage, Func<double, KeyframeVisual?> Visual)[] spans)
    {
        var start = episode.CreditsFingerprintStart;
        return Scan(Keyframes(0, episode.Duration - start, 2, [.. spans.Select(span => (span.From - start, span.To - start, span.Percentage, span.Visual))]));
    }

    /// <summary>
    /// Fingerprints that share one region so the chromaprint comparison finds exactly one
    /// shift; everything outside it differs in at least eight bits between any two episodes.
    /// </summary>
    private static uint[] SharedAudioFingerprint(QueuedEpisode episode, int sharedAudioLastPoint)
    {
        var mask = 0x11111111u * (uint)episode.EpisodeNumber;
        var points = new uint[3600];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = i >= SharedAudioFirstPoint && i <= sharedAudioLastPoint
                ? 0x40000000u + ((uint)i * 7919u)
                : (uint)i ^ mask;
        }

        return points;
    }

    /// <summary>
    /// Stages the season's fingerprints in the detection cache so an already-analyzed
    /// episode is re-queued for comparison when a sibling arrives.
    /// </summary>
    private static async Task CacheFingerprintsAsync(DetectionCacheService cache, StubFFmpegService ffmpeg, IEnumerable<QueuedEpisode> episodes)
    {
        foreach (var episode in episodes)
        {
            var points = await ffmpeg.FingerprintAsync(episode, AnalysisMode.Credits);
            cache.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.Chromaprint, WindowStart, Duration, points);
        }
    }
}
