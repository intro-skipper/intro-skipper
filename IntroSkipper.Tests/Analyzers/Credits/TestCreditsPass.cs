// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static IntroSkipper.Tests.BlackFrameFixtures;
using static IntroSkipper.Tests.StubFFmpegService;

/// <summary>
/// The credits pass over the stub ffmpeg: two 1000 second episodes whose credits window is
/// the last 450 seconds. Each episode's keyframe scan has a keyframe every 2 seconds across the
/// window. By default the pages from 950 to the end are a black roll whose first page follows a cut,
/// and each episode the keyframe analyzer runs on decodes the gap from the keyframe at 948. The
/// decode finds no cut unless a test says otherwise, so the roll starts on its first page. The shared
/// audio, when a test enables it, runs from about 748 to 983.
/// </summary>
public sealed class TestCreditsPass
{
    private const double Duration = 1000;
    private const double WindowStart = Duration - 450;
    private const double BlackStart = 950;
    private const int SharedAudioFirstPoint = 1600;
    private const int TitlesFirstPoint = 1300;
    private const int DefaultSharedAudioLastPoint = 3499;

    // The cut decode of the default roll, over the gap from the keyframe before it.
    private static readonly CutDecode RollProbe = new(948, 950);

    // The probe for the black a Chromaprint candidate opens on, over the 20 s before its start.
    private static readonly IntervalScan BlackBeforeProbe = new(PointTime(SharedAudioFirstPoint) - 20, PointTime(SharedAudioFirstPoint) + 1, 28, 85);

    [Theory]
    [InlineData(AnalyzerAction.Default, false)]
    [InlineData(AnalyzerAction.Default, true)]
    [InlineData(AnalyzerAction.Chapter, true)]
    [InlineData(AnalyzerAction.Chromaprint, false)]
    public async Task RecognizedCreditsChapter_IsAuthoritativeByDefault(AnalyzerAction action, bool ffmpegValid)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)]);
        var config = new PluginConfiguration { PreferChromaprint = true };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, action, ffmpegValid, CancellationToken.None);

        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
        foreach (var episode in episodes)
        {
            var row = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
            Assert.Equal(SegmentSource.Chapter, row.Source);
            Assert.Equal((900, 950), (row.ToSegment().Start, row.ToSegment().End));
            Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits));
        }
    }

    [Fact]
    public async Task AuthoritativeChapter_EndsOnTheMarkWithoutAKeyframeSnap()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(keyFrames: KeyframeAt(919.4), chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 920)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((900, 920, SegmentSource.Chapter), (row.ToSegment().Start, row.ToSegment().End, row.Source));
    }

    [Fact]
    public async Task EnhancedChapter_EndsOnTheMarkWithoutAKeyframeSnap()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(keyFrames: KeyframeAt(659.4), chapters: [Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var chapter = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId), s => s.Source == SegmentSource.Chapter).ToSegment();
        Assert.Equal((600, 660), (chapter.Start, chapter.End));
    }

    [Theory]
    [InlineData("Chapter 02", 900)]
    [InlineData("Ending", 995)]
    public async Task UnmatchedOrInvalidChapter_StillUsesCombinedDetection(string name, double start)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter(name, start)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(2, ffmpeg.FingerprintCalls);
        Assert.Equal(2, ffmpeg.KeyframeScanCalls);
        Assert.Equal(
            [RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe],
            ffmpeg.Calls);
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);
    }

    [Fact]
    public async Task AuthoritativeChapter_RespectsFullLengthChaptersBelowTheCreditsMinimum()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 970), Chapter("Preview", 975)]);
        var config = new PluginConfiguration { FullLengthChapters = true };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chapter, row.Source);
        Assert.Equal((970, 975), (row.ToSegment().Start, row.ToSegment().End));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(0, 60)]
    public async Task AuthoritativeChapter_ConsumedByOffsets_ClearsStaleCreditsWithoutFallback(int startOffset, int endOffset)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)]);
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(900, 950))], SegmentSource.Chapter);
        var config = new PluginConfiguration { IntroStartOffset = startOffset, IntroEndOffset = endOffset };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.All(episodes, episode => Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Credits)));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
    }

    [Fact]
    public async Task RejectedChapterCandidate_PreservesStandingCreditsWhenCombinedPassFindsNothing()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(spans: [], sharedAudioLastPoint: SharedAudioFirstPoint - 1, chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)]);
        var episode = episodes[0];
        Assert.Equal(1, await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(900, 950))],
            SegmentSource.Chapter));
        var chapter = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
        await database.DeleteSegmentAsync(episode.EpisodeId, chapter.Id);
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(700, 800))],
            SegmentSource.BlackFrame);
        var standingRow = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var preservedRow = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(standingRow.Id, preservedRow.Id);
        Assert.Equal(SegmentSource.BlackFrame, preservedRow.Source);
        Assert.Equal((700, 800), (preservedRow.ToSegment().Start, preservedRow.ToSegment().End));
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Credits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedSeason_ChapterResultSuppliesComparisonWithoutBeingReplaced(bool alreadyAnalyzed)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        episodes[0].Chapters = [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)];
        Guid? storedId = null;
        if (alreadyAnalyzed)
        {
            await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(900, 950))], SegmentSource.Chapter);
            episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
            storedId = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Id;
            await CacheFingerprintsAsync(cache, ffmpeg, [episodes[0]]);
        }

        await CreatePass(ffmpeg, database, cache, new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(1, ffmpeg.KeyframeScanCalls);
        Assert.Equal([RollProbe, BlackBeforeProbe], ffmpeg.Calls);
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

    [Fact]
    public async Task EnhancedChapter_SettledSiblingIsRecombined()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        episodes[0].Chapters = [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)];
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(900, 950))], SegmentSource.Chapter);
        episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        await CacheFingerprintsAsync(cache, ffmpeg, [episodes[0]]);

        await CreatePass(ffmpeg, database, cache, new PluginConfiguration { EnhanceChapterCredits = true }).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(2, ffmpeg.KeyframeScanCalls);
        var rows = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(row => row.StartTicks).Select(row => (row.ToSegment().Start, row.ToSegment().End, row.Source)).ToList();
        Assert.Equal(2, rows.Count);

        // The shared audio joins the chapter and stops at its end; the roll after it stays apart.
        Assert.Equal(PointTime(SharedAudioFirstPoint), rows[0].Start, 0.5);
        Assert.Equal((950, SegmentSource.Combined), (rows[0].End, rows[0].Source));
        Assert.Equal((BlackStart, Duration, SegmentSource.BlackFrame), rows[1]);
    }

    [Theory]
    [InlineData(AnalyzerAction.BlackFrame, SegmentSource.BlackFrame)]
    [InlineData(AnalyzerAction.Chromaprint, SegmentSource.Chromaprint)]
    public async Task ExplicitAnalyzerOverride_IgnoresAuthoritativeChapters(AnalyzerAction action, SegmentSource source)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, action, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(source, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);

        // Only the black-frame action runs the keyframe analyzer.
        Call[] probes = action switch { AnalyzerAction.BlackFrame => [RollProbe, RollProbe], AnalyzerAction.Chromaprint => [BlackBeforeProbe, BlackBeforeProbe], _ => [] };
        Assert.Equal(probes, ffmpeg.Calls);
    }

    [Fact]
    public async Task AuthoritativeChapters_DoNotReplaceUserProvidedCredits()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)]);
        await database.SeedUserSegmentAsync(episodes[0].EpisodeId, AnalysisMode.Credits, TimeSpan.FromSeconds(700).Ticks, TimeSpan.FromSeconds(720).Ticks);
        episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.UserProvided);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.User, row.Source);
        Assert.Equal((700, 720), (row.ToSegment().Start, row.ToSegment().End));
        Assert.Equal(EpisodeState.UserProvided, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
    }

    [Fact]
    public async Task CancellationBeforeChapterAnalysis_RethrowsWithoutPersistence()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900)]);
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

        Assert.Equal(WindowStart, ffmpeg.LastKeyframeScanStart);
        Assert.Equal(
            [RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe],
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

    [Fact]
    public async Task BlackRollBetweenAChapterAndTheSharedAudio_IsCombinedWithTheAudio()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([(700, Duration, 95, KeyframeVisuals.BlackAfterCut)], chapters: [Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(600, 660, SegmentSource.Chapter), (700, Duration, SegmentSource.Combined)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [new CutDecode(698, 700), BlackBeforeProbe, new CutDecode(698, 700), BlackBeforeProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task UnrelatedChapterAfterTheCredits_DoesNotCapTheTrailingRoll()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason([(900, Duration, 95, KeyframeVisuals.BlackAfterCut)], chapters: [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Epilogue", 760), Chapter("Chapter 04", 950)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(700, 760, SegmentSource.Chapter), (900, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [new CutDecode(898, 900), new CutDecode(898, 900)],
            ffmpeg.Calls);
    }

    // Story runs from the credits chapter to the end of the file, or through an epilogue to a
    // preview chapter. The chapter row keeps its mark: the keyframe just before it is not taken.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoryAfterTheCreditsChapter_CombinesItWithTheClosingRoll(bool previewAfterTheEpilogue)
    {
        using var scope = Scope();
        ChapterInfo[] chapters = previewAfterTheEpilogue
            ? [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Epilogue", 760), Chapter("Preview", 960)]
            : [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Epilogue", 760)];
        var (episodes, ffmpeg, database) = CreateSeason(keyFrames: KeyframeAt(759.4), chapters: chapters);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration())
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        foreach (var episode in episodes)
        {
            var segments = (await database.GetSegmentsAsync(episode.EpisodeId)).OrderBy(s => s.StartTicks).ToList();
            Assert.Equal(
                [(700, 760, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
                segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        }

        Assert.Equal([RollProbe, RollProbe], ffmpeg.Calls);
    }

    // A black roll inside a chapter with story after it merges the chapter into a Combined row
    // that ends on the mark. The mark rule keys on the source, so the row is snapped like any
    // detected range.
    [Fact]
    public async Task RollInsideTheCreditsChapter_MergesItIntoACombinedRowThatIsSnapped()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(620, 660, 95, KeyframeVisuals.BlackAfterCut), (BlackStart, Duration, 95, KeyframeVisuals.BlackAfterCut)],
            keyFrames: KeyframeAt(659.4),
            chapters: [Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(600, 659.4, SegmentSource.Combined), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    // No story fits after the chapter: a tail under the credits minimum, the first of two preview
    // chapters right after it, and padding past the end of the audio, where the window ends,
    // with or without a preview chapter in the silent tail.
    [Theory]
    [InlineData(0, 900, 990)]
    [InlineData(1, 700, 760)]
    [InlineData(2, 900, 960)]
    [InlineData(3, 900, 960)]
    public async Task NoStoryAfterTheCreditsChapter_KeepsItAuthoritative(int layout, double start, double end)
    {
        using var scope = Scope();
        ChapterInfo[] chapters = layout switch
        {
            0 => [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Logo", 990)],
            1 => [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Preview", 760), Chapter("Omake", 780), Chapter("Extra", 960)],
            2 => [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Padding", 960)],
            _ => [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Padding", 960), Chapter("Preview", 980)],
        };
        var (episodes, ffmpeg, database) = CreateSeason(chapters: chapters);
        if (layout >= 2)
        {
            episodes.ForEach(episode => episode.CreditsFingerprintEnd = 970);
        }

        await CreatePass(ffmpeg, database, config: new PluginConfiguration())
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((start, end, SegmentSource.Chapter), (row.ToSegment().Start, row.ToSegment().End, row.Source));
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
    }

    // The 50 s preview chapter is shorter than the preview minimum or longer than the maximum.
    [Theory]
    [InlineData(60, 120)]
    [InlineData(15, 30)]
    public async Task PreviewChapterOutsideItsDurationLimits_DoesNotEndTheStory(int minimumPreviewDuration, int maximumPreviewDuration)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)]);
        var config = new PluginConfiguration { MinimumPreviewDuration = minimumPreviewDuration, MaximumPreviewDuration = maximumPreviewDuration };

        await CreatePass(ffmpeg, database, config: config)
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(900, 950, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    // Exactly the credits minimum between the chapter end and the preview chapter is story.
    [Fact]
    public async Task MinimumCreditsDurationBeforeThePreviewChapter_IsStory()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950), Chapter("Preview", 965)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration())
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(900, 950, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    // A season a chapter settled before the story rule: the comparison rerun for a new episode
    // gives a sibling a Chromaprint candidate past its stored chapter, and the sibling is
    // recombined like a pending episode.
    [Fact]
    public async Task SettledSibling_WithStoryAfterItsChapter_IsRecombined()
    {
        using var scope = Scope();
        ChapterInfo[] chapters = [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Epilogue", 760)];
        var (episodes, ffmpeg, database) = CreateSeason(chapters: chapters);
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        foreach (var episode in episodes)
        {
            await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits, [new Segment(episode.EpisodeId, new TimeRange(700, 760))], SegmentSource.Chapter);
            episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        }

        await CacheFingerprintsAsync(cache, ffmpeg, episodes);
        var added = Episode(episodes[0].SeasonId, 3);
        added.Chapters = chapters;
        episodes.Add(added);

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(700, 760, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    [Theory]
    [InlineData(false, SegmentSource.Chapter)]
    [InlineData(true, SegmentSource.Combined)]
    public async Task ChapteredPreviewAfterTheCredits_IsNeitherExtendedNorMergedInto(bool enhanceChapters, SegmentSource source)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 980)]);
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Preview, [new Segment(episodes[0].EpisodeId, new TimeRange(980, Duration))], SegmentSource.CreditsDerived);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = enhanceChapters })
            .RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);
        await AnimePreviewDeriver.DeriveAsync(database, episodes, 15, CancellationToken.None);

        var rows = await database.GetSegmentsAsync(episodes[0].EpisodeId);
        var credits = Assert.Single(rows, s => s.Type == AnalysisMode.Credits);
        Assert.Equal((900, 980, source), (credits.ToSegment().Start, credits.ToSegment().End, credits.Source));
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
        Assert.Equal([RollProbe, BlackBeforeProbe], ffmpeg.Calls);
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

    // The keyframe roll is the failing episode's only result, and an intro over it makes
    // admission refuse it. A refusal recurs on every retry, so it settles the episode like a
    // written result, and the standing credits row stays as it was.
    [Fact]
    public async Task FingerprintFailure_WithEveryCandidateRefused_SettlesAndKeepsStandingCredits()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(fingerprintFailure: episode => episode.EpisodeNumber == 1 ? new FingerprintException("no audio") : null);
        var failing = episodes[0];
        await database.ReplaceAutoSegmentsAsync(failing.EpisodeId, AnalysisMode.Introduction, [new Segment(failing.EpisodeId, new TimeRange(BlackStart - 10, BlackStart + 10))], SegmentSource.Chromaprint);
        await database.ReplaceAutoSegmentsAsync(failing.EpisodeId, AnalysisMode.Credits, [new Segment(failing.EpisodeId, new TimeRange(700, 720))], SegmentSource.BlackFrame);
        var standing = Assert.Single(await database.GetSegmentsAsync(failing.EpisodeId), s => s.Type == AnalysisMode.Credits);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var kept = Assert.Single(await database.GetSegmentsAsync(failing.EpisodeId), s => s.Type == AnalysisMode.Credits);
        Assert.Equal((standing.Id, 700.0, 720.0), (kept.Id, kept.ToSegment().Start, kept.ToSegment().End));
        Assert.Equal(EpisodeState.Analyzed, failing.GetAnalyzed(AnalysisMode.Credits));
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

    // The shared audio starts at PointTime(SharedAudioFirstPoint), and the intervals are given as
    // (start, end) offsets from it; the probe reads the 20 s before it.
    [Theory]
    [InlineData(new[] { -3.0, -0.4, -0.3, 0.2 }, -3.0)] // a black split by a fade-in walks back as one
    [InlineData(new[] { -4.0, -1.5 }, 0.0)]              // a black ending over 1 s before moves nothing
    [InlineData(new[] { -20.0, -0.5 }, 0.0)]             // a black from the probe's start may have begun earlier
    [InlineData(new[] { -6.0, -2.5, -0.5, 0.1 }, -0.5)] // a piece over 1 s before the next one is apart
    [InlineData(new[] { -5.0, -1.3, -1.2, 0.0 }, -5.0)] // a piece ending over 1 s before the start joins through the next one
    [InlineData(new[] { -19.96, -0.5 }, 0.0)]            // a black a frame after the probe's start may have begun earlier too
    [InlineData(new[] { 0.3, 0.9 }, 0.0)]                // a black after the start moves nothing
    public async Task ChromaprintCredits_StartOnTheBlackTheyOpenOn(double[] blacks, double expectedOffset)
    {
        using var scope = Scope();
        var start = PointTime(SharedAudioFirstPoint);
        BlackInterval[] intervals = [.. blacks.Chunk(2).Select(black => new BlackInterval(start + black[0] - WindowStart, start + black[1] - WindowStart))];
        var (episodes, ffmpeg, database) = CreateSeason(blackIntervals: range => range.End > start ? intervals : []);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            Assert.Equal(start + expectedOffset, Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId)).ToSegment().Start, 3);
        }
    }

    // The chapter end sits inside the black probe's look-back; a black after it still moves the start.
    [Fact]
    public async Task ChromaprintCredits_StartOnTheBlackAfterAChapterEnd()
    {
        using var scope = Scope();
        var start = PointTime(SharedAudioFirstPoint);
        var (episodes, ffmpeg, database) = CreateSeason(
            chapters: [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Preview", start - 15)],
            blackIntervals: range => range.End > start ? [new BlackInterval(start - 4 - WindowStart, start - 0.2 - WindowStart)] : []);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true }).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            var segments = await database.GetSegmentsAsync(episode.EpisodeId);
            Assert.Contains(segments, segment => segment.Source != SegmentSource.Chapter
                && Math.Abs(segment.ToSegment().Start - (start - 4)) < 0.001
                && segment.ToSegment().End == Duration);
        }
    }

    // A credits chapter ends 1 s before the shared audio and a black runs across that end. Moved
    // onto the black, the candidate would be capped at the chapter's end and dropped as too short,
    // leaving only the roll at 950. Kept, it starts at the chapter's end, where the time adjustment
    // snaps it.
    [Fact]
    public async Task ChromaprintCredits_KeepTheirStartWhenTheBlackCrossesAChapterEnd()
    {
        using var scope = Scope();
        var start = PointTime(SharedAudioFirstPoint);
        var (episodes, ffmpeg, database) = CreateSeason(
            chapters: [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Preview", start - 1)],
            blackIntervals: range => range.End > start ? [new BlackInterval(start - 4 - WindowStart, start - 0.2 - WindowStart)] : []);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true }).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            var segments = await database.GetSegmentsAsync(episode.EpisodeId);
            Assert.Contains(segments, segment => segment.Source != SegmentSource.Chapter
                && segment.ToSegment().Start >= start - 1 - 0.001
                && segment.ToSegment().Start <= start
                && segment.ToSegment().End == Duration);
        }
    }

    // The titles match on another alignment than the roll, 12 s and 17 s before it, and join it.
    [Fact]
    public async Task TitlesOnAnotherAlignment_JoinTheRoll()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(fingerprints: TitlesThenRoll);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(PointTime(TitlesFirstPoint), Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).ToSegment().Start, 0.5);
        Assert.Equal(PointTime(TitlesFirstPoint - 40), Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).ToSegment().Start, 0.5);
    }

    // The same titles, with a credits chapter ending between them and the roll. Joined, the region
    // would start before the chapter's end, be capped there and lose the roll; it keeps the roll.
    [Fact]
    public async Task TitlesOnAnotherAlignment_NeverJoinTheRollAcrossAChapterEnd()
    {
        using var scope = Scope();
        var boundary = PointTime(1550);
        var (episodes, ffmpeg, database) = CreateSeason(
            fingerprints: TitlesThenRoll,
            chapters: [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Preview", boundary)]);

        await CreatePass(ffmpeg, database, config: new PluginConfiguration { EnhanceChapterCredits = true }).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            var segments = await database.GetSegmentsAsync(episode.EpisodeId);
            Assert.Contains(segments, segment => segment.Source != SegmentSource.Chapter
                && segment.ToSegment().Start >= boundary - 0.001
                && segment.ToSegment().Start <= PointTime(SharedAudioFirstPoint) + 0.001
                && segment.ToSegment().End == Duration);
        }
    }

    [Fact]
    public async Task TitlesOnAnotherAlignment_NeverJoinAcrossASettledSiblingsChapterEnd()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(fingerprints: TitlesThenRoll);
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        var boundary = PointTime(1550);
        episodes[0].Chapters = [Chapter("Main", 0), Chapter("Ending", 700), Chapter("Preview", boundary)];
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(700, boundary))], SegmentSource.Chapter);
        episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        await CacheFingerprintsAsync(cache, ffmpeg, [episodes[0]]);

        await CreatePass(ffmpeg, database, cache, new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(PointTime(SharedAudioFirstPoint), Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).ToSegment().Start, 0.5);
    }

    [Fact]
    public async Task ChromaprintCredits_KeepTheirStartWhenTheBlackProbeFails()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(blackIntervals: _ => throw new InvalidOperationException("probe failed"));

        await CreatePass(ffmpeg, database, config: new PluginConfiguration()).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        foreach (var episode in episodes)
        {
            Assert.Equal(PointTime(SharedAudioFirstPoint), Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId)).ToSegment().Start, 3);
        }
    }

    [Fact]
    public async Task SettledSibling_WhoseCandidateIsAlreadyStored_IsNotRescanned()
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
        var scansBefore = ffmpeg.KeyframeScanCalls;

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(scansBefore + 1, ffmpeg.KeyframeScanCalls);
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[2].EpisodeId)).Source);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(storedIds[i], Assert.Single(await database.GetSegmentsAsync(episodes[i].EpisodeId)).Id);
        }

        // Both runs: two episodes in the first, only the new one in the second.
        Assert.Equal(
            [RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task SettledSibling_WhoseCandidateReachesOutsideStoredCredits_IsRecombined()
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

        // Both runs share the cache: two episodes decode in the first; in the second the recombined
        // episode reads its cut start from the cache and only the new one decodes.
        Assert.Equal(
            [RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe],
            ffmpeg.Calls);
    }

    // A failed keyframe scan costs the episode its keyframe candidates, and the Chromaprint
    // candidate settles it, as a failed fingerprint lets the keyframe roll settle an episode.
    [Fact]
    public async Task KeyframeScanFailure_SettlesTheEpisodeOnItsOtherCandidates()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(scanFailure: episode => episode.EpisodeNumber == 1 ? new InvalidOperationException("scan failed") : null);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var written = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chromaprint, written.Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), written.ToSegment().Start, 0.5);
        Assert.Equal(PointTime(DefaultSharedAudioLastPoint), written.ToSegment().End, 0.5);
        Assert.Equal(EpisodeState.Analyzed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
        Assert.Equal(2, ffmpeg.KeyframeScanCalls);
        Assert.Equal([BlackBeforeProbe, RollProbe, BlackBeforeProbe], ffmpeg.Calls);
    }

    // With the keyframe analyzer as the only analyzer in the pass, its failure leaves nothing to
    // settle the episode on: nothing is written and the next pass retries it.
    [Fact]
    public async Task KeyframeScanFailure_WithNoOtherCandidate_FailsThatEpisodeAndContinues()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(scanFailure: episode => episode.EpisodeNumber == 1 ? new InvalidOperationException("scan failed") : null);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(SegmentSource.BlackFrame, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
        Assert.Equal([RollProbe], ffmpeg.Calls);
    }

    // A pending episode with standing credits, from a scan under another configuration or an
    // earlier persistence failure, loses nothing to a failed keyframe scan: its Chromaprint
    // candidate alone would drop the roll's tail, so it is not written. The roll keeps its id and
    // the next pass retries the episode.
    [Fact]
    public async Task KeyframeScanFailure_NeverReplacesStandingCredits()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(scanFailure: episode => episode.EpisodeNumber == 1 ? new InvalidOperationException("scan failed") : null);
        var failing = episodes[0];
        await database.ReplaceAutoSegmentsAsync(failing.EpisodeId, AnalysisMode.Credits, [new Segment(failing.EpisodeId, new TimeRange(BlackStart, Duration))], SegmentSource.BlackFrame);
        var standing = Assert.Single(await database.GetSegmentsAsync(failing.EpisodeId));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var kept = Assert.Single(await database.GetSegmentsAsync(failing.EpisodeId));
        Assert.Equal((standing.Id, SegmentSource.BlackFrame, BlackStart, Duration), (kept.Id, kept.Source, kept.ToSegment().Start, kept.ToSegment().End));
        Assert.Equal(EpisodeState.AnalysisFailed, failing.GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
        Assert.Equal([RollProbe, BlackBeforeProbe], ffmpeg.Calls);
    }

    // A settled sibling is rescanned for a Chromaprint candidate reaching outside its stored
    // credits. When that rescan fails, writing the candidate alone would lose the roll's tail, so
    // the roll stays and the sibling is still settled. The failed keyframe scan is attempted even
    // though its cut decode never runs.
    [Fact]
    public async Task SettledSibling_WhoseKeyframeScanFails_KeepsItsStoredCredits()
    {
        using var scope = Scope();
        var failScans = false;
        var (episodes, ffmpeg, database) = CreateSeason(scanFailure: episode => failScans && episode.EpisodeNumber == 1 ? new InvalidOperationException("scan failed") : null);
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits, [new Segment(episodes[0].EpisodeId, new TimeRange(BlackStart, Duration))], SegmentSource.BlackFrame);
        var roll = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        await CacheFingerprintsAsync(cache, ffmpeg, episodes);
        episodes.Add(Episode(episodes[0].SeasonId, 3));
        failScans = true;

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var kept = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((roll.Id, SegmentSource.BlackFrame, BlackStart, Duration), (kept.Id, kept.Source, kept.ToSegment().Start, kept.ToSegment().End));
        Assert.Equal(EpisodeState.Analyzed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[2].EpisodeId)).Source);
        Assert.Equal(4, ffmpeg.KeyframeScanCalls);

        // Both runs: two scans in the first; in the second, the new episode decodes while the
        // settled sibling's failing scan stops before its cut decode.
        Assert.Equal(
            [RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe, RollProbe, BlackBeforeProbe],
            ffmpeg.Calls);
    }

    [Theory]
    [InlineData(AnalyzerAction.Default)]
    [InlineData(AnalyzerAction.BlackFrame)]
    public async Task CardCreditsBeforeTheBlackRoll_AreCombinedWithIt(AnalyzerAction action)
    {
        // The black-frame action restricts the pass to the keyframe analyzer and keeps its card candidate.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.BlackAfterCut)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, action, ffmpegValid: false, CancellationToken.None);

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
    public async Task CardCreditsSeparatedFromTheBlackRoll_AreStoredApart()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(800, 830, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.BlackAfterCut)]);

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
    public async Task DarkLeadInBeforeTheBlackRoll_StaysOutOfTheCardCandidate()
    {
        // 900 to 940 is 90 percent black and a black page to the visuals, 942 to 978 is the roll, cards
        // follow to the end. The black-frame transition check starts the roll at 942; the card candidate
        // must not reach back into the lead-in. The cut decode reads the gap from the lead-in's last
        // keyframe.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(900, 940, 90, KeyframeVisuals.BlackAfterCut), (942, 978, 100, KeyframeVisuals.BlackAfterCut), (980, Duration, 0, KeyframeVisuals.Card)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((942, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        Assert.Equal(
            [new CutDecode(940, 942), new CutDecode(940, 942)],
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
            [(800, 828, 100, KeyframeVisuals.BlackAfterCut), (830, 906, 60, KeyframeVisuals.Dark), (908, Duration, 100, KeyframeVisuals.BlackAfterCut)]);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(800, 828, SegmentSource.BlackFrame), (908, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
        Assert.Equal(
            [
                new CutDecode(798, 800),
                new CutDecode(906, 908),
                new CutDecode(798, 800),
                new CutDecode(906, 908),
            ],
            ffmpeg.Calls);
    }

    [Theory]
    [InlineData(800.0)]
    [InlineData(BlackStart)]
    public async Task CutDecodeFailureOnAnyAcceptedScene_FailsTheEpisode(double failingSceneStart)
    {
        // A roll from 950 sits beside an accepted scene from 800 to 830, and both get the cut
        // decode. A decode that throws fails the scan whichever scene it belongs to; with no other
        // candidate nothing is written and the next pass retries.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(800, 830, 100, KeyframeVisuals.BlackAfterCut), (BlackStart, Duration, 100, KeyframeVisuals.BlackAfterCut)],
            cutDecode: (_, window) => window.End == failingSceneStart ? throw new InvalidOperationException("decode failed") : null);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));

        // Scenes are probed in time order, and the decode that throws is each episode's last.
        Call[] episodeProbes = failingSceneStart == 800
            ? [new CutDecode(798, 800)]
            : [new CutDecode(798, 800), RollProbe];
        Assert.Equal([.. episodeProbes, .. episodeProbes], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectNonBlackCreditsOff_IgnoresCardCredits()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.BlackAfterCut)]);
        var config = new PluginConfiguration { DetectNonBlackCredits = false };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((BlackStart, Duration, SegmentSource.BlackFrame), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        Assert.Equal(
            [RollProbe, RollProbe],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task LegacyBlackFrameAnalyzer_FeedsThePassUnderItsToggle()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            [(BlackStart - 60, BlackStart - 2, 0, KeyframeVisuals.Card), (BlackStart, Duration, 95, KeyframeVisuals.BlackAfterCut)],
            rangeScan: (_, range, _, _, _) => range.Start is >= BlackStart and < Duration ? [new BlackFrame(95, 0, 0)] : [], chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950)]);
        var config = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, UseChapterMarkersBlackFrame = false, EnhanceChapterCredits = true };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
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
    public async Task OneNewChapteredEpisode_SettlesByItsChapterWithoutFingerprintsOrScans(bool ffmpegValid)
    {
        using var scope = Scope();
        var (_, ffmpeg, database) = CreateSeason();
        var seasonId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 26).Select(number => Episode(seasonId, number)).ToList();
        foreach (var episode in episodes)
        {
            episode.Chapters = [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)];
        }

        foreach (var episode in episodes.Take(25))
        {
            episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        }

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid, CancellationToken.None);

        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(0, ffmpeg.KeyframeScanCalls);
        Assert.Equal(SegmentSource.Chapter, Assert.Single(await database.GetSegmentsAsync(episodes[25].EpisodeId)).Source);
    }

    [Fact]
    public async Task CoveredSettledSibling_IsNeitherRescannedNorReplaced()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits,
            [new Segment(episodes[0].EpisodeId, new TimeRange(700, Duration))], SegmentSource.Combined);
        episodes[0].SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        await CacheFingerprintsAsync(cache, ffmpeg, [episodes[0]]);
        var storedId = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Id;

        await CreatePass(ffmpeg, database, cache).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(1, ffmpeg.KeyframeScanCalls);
        Assert.Equal([RollProbe, BlackBeforeProbe], ffmpeg.Calls);
        Assert.Equal(storedId, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Id);
        Assert.All(episodes, episode => Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits)));
    }

    /// <summary>
    /// A two-episode season where the offsets consume the chaptered episode's chapter: its audio
    /// must still serve as the reference, or the unchaptered sibling has nothing to compare
    /// against and would be recorded as having no credits.
    /// </summary>
    [Fact]
    public async Task UnusableChapterResult_StillSuppliesComparisonWithoutBeingReplaced()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        episodes[0].Chapters = [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 920)];
        var config = new PluginConfiguration { IntroEndOffset = 30 };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(EpisodeState.NoSegments, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(2, ffmpeg.FingerprintCalls);
        Assert.Equal(1, ffmpeg.KeyframeScanCalls);
        Assert.Equal([RollProbe, BlackBeforeProbe], ffmpeg.Calls);
        Assert.Equal(EpisodeState.Analyzed, episodes[1].GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
    }

    /// <summary>
    /// A usable chapter whose write fails marks its episode failed, but its audio still serves
    /// as the reference for the unchaptered sibling, and the episode is not analyzed again.
    /// </summary>
    [Fact]
    public async Task FailedChapterWrite_StillSuppliesComparisonWithoutBeingReplaced()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, _) = CreateSeason();
        episodes[0].Chapters = [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 920)];
        var database = FailingSegmentWrites(episodes[0].EpisodeId);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(2, ffmpeg.FingerprintCalls);
        Assert.Equal(1, ffmpeg.KeyframeScanCalls);
        Assert.Equal([RollProbe, BlackBeforeProbe], ffmpeg.Calls);
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
        episodes[chapteredIndex].Chapters = [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)];
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
    public async Task SettledCombinedCredits_SurviveSiblingAnalysis()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(chapters: [Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 950)]);
        var config = new PluginConfiguration();
        const string currentHash = "7EF68040EE4E4EC7";
        Assert.Equal(currentHash, ConfigHasher.Analysis(config, AnalysisMode.Credits, AnalyzerAction.Default, true));
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Credits,
            [new Segment(episodes[0].EpisodeId, new TimeRange(750, Duration))], SegmentSource.Combined, currentHash);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [episodes[0].EpisodeId], currentHash);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(episodes[0].SeasonId, episodes.Select(e => e.EpisodeId).ToArray());
        var verifier = new QueueVerifier(config, [AnalysisMode.Credits], snapshot, true);
        foreach (var episode in episodes)
        {
            verifier.Classify(episode);
            episode.AnalysisConfigHash = currentHash;
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

    private static IDisposable Scope()
        => EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());

    private static ChapterInfo Chapter(string name, double start)
        => new() { Name = name, StartPositionTicks = TimeSpan.FromSeconds(start).Ticks };

    // A keyframe lookup that finds one keyframe, when the queried range holds it.
    private static Func<QueuedEpisode, TimeRange, AnalysisMode, double[]> KeyframeAt(double time)
        => (_, range, _) => range.Start <= time && time <= range.End ? [time] : [];

    // A segment database whose writes of the item's segments fail.
    private static IntroSkipperDatabase FailingSegmentWrites(Guid itemId)
    {
        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>(DatabaseTestHelpers.CreateContextOptions<IntroSkipperDbContext>(DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db")))
            .AddInterceptors(new FailSegmentWrites(itemId))
            .Options;
        return new IntroSkipperDatabase(new TestDbContextFactory<IntroSkipperDbContext>(() => new IntroSkipperDbContext(options)), NullLogger<IntroSkipperDatabase>.Instance);
    }

    private static CreditsPass CreatePass(StubFFmpegService ffmpeg, IntroSkipperDatabase database, DetectionCacheService? cache = null, PluginConfiguration? config = null)
        => new(NullLoggerFactory.Instance, ffmpeg, cache ?? DatabaseTestHelpers.CreateTempCacheService(), database, config ?? new PluginConfiguration());

    /// <summary>
    /// A two-episode season over the stub. Each episode's keyframe scan comes from one keyframe list
    /// (see <see cref="ScanOf"/>). When <paramref name="spans"/> is null the pages from 950 to the end
    /// are a 95 percent black roll with lettering whose first page follows a cut; an empty list gives
    /// busy content only. Range scans, which only the legacy analyzer runs, find nothing unless
    /// <paramref name="rangeScan"/> says otherwise, blackdetect finds no intervals, and cut decodes
    /// find no cut unless <paramref name="cutDecode"/> says otherwise, so no probe throws for want of a hook.
    /// Silence and keyframe lookups return nothing so end adjustment leaves ends alone.
    /// </summary>
    private static (List<QueuedEpisode> Episodes, StubFFmpegService Ffmpeg, IntroSkipperDatabase Database) CreateSeason(
        (double From, double To, int Percentage, Func<double, KeyframeVisual?> Visual)[]? spans = null,
        int sharedAudioLastPoint = DefaultSharedAudioLastPoint,
        Func<QueuedEpisode, Exception?>? fingerprintFailure = null,
        Func<QueuedEpisode, Exception?>? scanFailure = null,
        Func<QueuedEpisode, TimeRange, int, int, AnalysisMode, BlackFrame[]>? rangeScan = null,
        Func<QueuedEpisode, TimeRange, KeyframeVisual[]?>? cutDecode = null,
        Func<QueuedEpisode, TimeRange, AnalysisMode, double[]>? keyFrames = null,
        ChapterInfo[]? chapters = null,
        Func<TimeRange, BlackInterval[]>? blackIntervals = null,
        Func<QueuedEpisode, uint[]>? fingerprints = null)
    {
        var seasonId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 2).Select(number => Episode(seasonId, number)).ToList();
        episodes.ForEach(episode => episode.Chapters = chapters ?? []);
        var scanSpans = spans ?? [(BlackStart, Duration, 95, KeyframeVisuals.BlackAfterCut)];

        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => fingerprintFailure?.Invoke(episode) is { } failure
                ? throw failure
                : fingerprints?.Invoke(episode) ?? SharedAudioFingerprint(episode, sharedAudioLastPoint),
            KeyframeScan = (episode, _) => scanFailure?.Invoke(episode) is { } failure
                ? throw failure
                : ScanOf(episode, scanSpans),
            RangeBlackFrames = rangeScan ?? ((_, _, _, _, _) => []),
            BlackIntervals = (_, range, _, _) => blackIntervals?.Invoke(range) ?? [],
            CutWindows = cutDecode ?? ((_, _) => []),
            Silence = (_, _, _) => [],
            KeyFrames = keyFrames ?? ((_, _, _) => []),
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
    /// One episode's keyframe scan: a page every two seconds across the credits window, with its
    /// black row and its visual, relative to the credits window start like the scan reports them. A
    /// keyframe inside one of the <paramref name="spans"/>, given in file time, takes that span's
    /// black percentage and visual; any other keyframe is busy content, not black.
    /// </summary>
    private static KeyframePage[] ScanOf(QueuedEpisode episode, (double From, double To, int Percentage, Func<double, KeyframeVisual?> Visual)[] spans)
    {
        var start = episode.CreditsFingerprintStart;
        return Pages(Keyframes(0, episode.Duration - start, 2, [.. spans.Select(span => (span.From - start, span.To - start, span.Percentage, span.Visual))]));
    }

    /// <summary>
    /// Fingerprints that share one region so the chromaprint comparison finds exactly one
    /// shift; everything outside it differs in at least eight bits between any two episodes.
    /// </summary>
    // Styled titles shared at points 1300-1499 in episode 1 and 1260-1459 in episode 2, a second
    // alignment, and the default roll from SharedAudioFirstPoint on the first.
    private static uint[] TitlesThenRoll(QueuedEpisode episode)
    {
        var points = SharedAudioFingerprint(episode, DefaultSharedAudioLastPoint);
        var titlesStart = episode.EpisodeNumber == 1 ? TitlesFirstPoint : TitlesFirstPoint - 40;
        for (var k = 0; k < 200; k++)
        {
            points[titlesStart + k] = 0x50000000u + ((uint)k * 104729u);
        }

        return points;
    }

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

    private sealed class FailSegmentWrites(Guid itemId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => eventData.Context?.ChangeTracker.Entries<DbSegment>().Any(entry => entry.Entity.ItemId == itemId) == true
                ? throw new InvalidOperationException("Segment write failed")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
