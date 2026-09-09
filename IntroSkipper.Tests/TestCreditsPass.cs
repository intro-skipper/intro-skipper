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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static IntroSkipper.Tests.BlackFrameFixtures;

/// <summary>
/// The credits pass over the stub ffmpeg: two 1000 second episodes whose credits window is
/// the last 450 seconds. Black frames run from 950 to 999.5 and the shared audio, when a
/// test enables it, from about 748 to 983.
/// </summary>
public sealed class TestCreditsPass
{
    private const double Duration = 1000;
    private const double WindowStart = Duration - 450;
    private const double BlackStart = 950;
    private const int SharedAudioFirstPoint = 1600;
    private const int DefaultSharedAudioLastPoint = 3499;

    [Fact]
    public async Task OverlappingBlackFrameAndChromaprint_WriteOneCombinedSegment()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(WindowStart, ffmpeg.LastCreditsScanStart);
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
        var (episodes, ffmpeg, database) = CreateSeason(blackStart: 700, sharedAudioLastPoint: sharedAudioLastPoint);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Combined, segment.Source);
        Assert.Equal(700, segment.ToSegment().Start);
        Assert.Equal(Duration, segment.ToSegment().End);
    }

    [Fact]
    public async Task BlackRollBetweenAChapterAndTheSharedAudio_IsCombinedWithTheAudio()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660));
        var (episodes, ffmpeg, database) = CreateSeason(blackStart: 700);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(600, 660, SegmentSource.Chapter), (700, Duration, SegmentSource.Combined)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    [Fact]
    public async Task UnrelatedChapterAfterTheCredits_DoesNotCapTheTrailingRoll()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 700), Chapter("Epilogue", 760), Chapter("Chapter 04", 950));
        var (episodes, ffmpeg, database) = CreateSeason(blackStart: 900);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(700, 760, SegmentSource.Chapter), (900, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    [Fact]
    public async Task ChapteredPreviewAfterTheCredits_IsNeitherExtendedNorMergedInto()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Preview", 980));
        var (episodes, ffmpeg, database) = CreateSeason();
        await database.ReplaceAutoSegmentsAsync(episodes[0].EpisodeId, AnalysisMode.Preview, [new Segment(episodes[0].EpisodeId, new TimeRange(980, Duration))], SegmentSource.CreditsDerived);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);
        await AnimePreviewDeriver.DeriveAsync(database, episodes, 15, CancellationToken.None);

        var rows = await database.GetSegmentsAsync(episodes[0].EpisodeId);
        var credits = Assert.Single(rows, s => s.Type == AnalysisMode.Credits).ToSegment();
        Assert.Equal((900, 980), (credits.Start, credits.End));
        var preview = Assert.Single(rows, s => s.Type == AnalysisMode.Preview).ToSegment();
        Assert.Equal((980, Duration), (preview.Start, preview.End));
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
        var (episodes, ffmpeg, database) = CreateSeason(blackFrames: false, sharedAudioLastPoint: 3560); // shared audio to about 991 of 1000

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chromaprint, segment.Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), segment.ToSegment().Start, 0.5);
        Assert.Equal(Duration, segment.ToSegment().End);
    }

    [Theory]
    [InlineData(700.0, 720.0)]
    [InlineData(650.0, 700.0)]
    public async Task BlackCreditsSeparatedFromTheSharedAudio_AreKept(double blackStart, double blackEnd)
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(blackStart: blackStart, blackEnd: blackEnd);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal((blackStart, blackEnd, SegmentSource.BlackFrame), (segments[0].ToSegment().Start, segments[0].ToSegment().End, segments[0].Source));
        Assert.Equal(SegmentSource.Chromaprint, segments[1].Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), segments[1].ToSegment().Start, 0.5);
        Assert.Equal(PointTime(DefaultSharedAudioLastPoint), segments[1].ToSegment().End, 0.5);
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
    }

    [Fact]
    public async Task BlackFrameAfterTheChapterFollowingTheCredits_StaysItsOwnSegment()
    {
        // CITY THE ANIMATION E08: the dubbing cards sit inside the "epilogue" chapter that
        // follows the ending chapter, so the authored boundary keeps them apart.
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(900, 950, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    [Fact]
    public async Task CandidatesSeparatedByContent_AreWrittenApartUnderTheirOwnSources()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 600), Chapter("Epilogue", 660));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(600, 660, SegmentSource.Chapter), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
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
    }

    [Fact]
    public async Task FingerprintFailure_WritesTheOtherCandidatesAndSettlesTheEpisode()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(fingerprintFailure: episode => episode.EpisodeNumber == 1 ? new FingerprintException("no audio") : null);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(SegmentSource.BlackFrame, Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId)).Source);
        Assert.All(episodes, episode => Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Credits)));
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

        // The new episode's keyframe scan and the card analyzer's read of the same row.
        Assert.Equal(scansBefore + 2, ffmpeg.CreditsScanCalls);
        Assert.Equal(SegmentSource.Combined, Assert.Single(await database.GetSegmentsAsync(episodes[2].EpisodeId)).Source);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(storedIds[i], Assert.Single(await database.GetSegmentsAsync(episodes[i].EpisodeId)).Id);
        }
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
    }

    [Fact]
    public async Task CancellationBeforeAnEpisode_Rethrows()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, cancellation.Token));
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
    }

    [Fact]
    public async Task BlackFrameScanFailure_FailsThatEpisodeAndContinues()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            creditsScan: (episode, _) => episode.EpisodeNumber == 1 ? throw new InvalidOperationException("scan failed") : BlackFramesFrom(episode));
        var failing = episodes[0].EpisodeId;

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Credits));
        Assert.True(episodes[0].NeedsAnalysis(AnalysisMode.Credits));
        Assert.Empty(await database.GetSegmentsAsync(failing));
        Assert.Equal(SegmentSource.BlackFrame, Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId)).Source);
    }

    [Fact]
    public async Task CardCreditsBeforeTheBlackRoll_AreCombinedWithIt()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(keyframeVisuals: episode => CardVisualsFrom(episode, BlackStart - 60, Duration));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        foreach (var episode in episodes)
        {
            var segment = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
            Assert.Equal((BlackStart - 60, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
        }
    }

    [Fact]
    public async Task CardCreditsAlone_AreStoredUnderTheirOwnSource()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            blackFrames: false,
            keyframeVisuals: episode => CardVisualsFrom(episode, Duration - 60, Duration, blackRoll: false));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((Duration - 60, Duration, SegmentSource.KeyframeVisuals), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
    }

    [Fact]
    public async Task CardCreditsSeparatedFromTheBlackRoll_AreStoredApart()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(keyframeVisuals: episode => CardVisualsFrom(episode, 800, 830));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(800, 830, SegmentSource.KeyframeVisuals), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    [Fact]
    public async Task ShortCardCreditsBeforeAShortBlackRoll_QualifyTogether()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            blackStart: 988,
            keyframeVisuals: episode => CardVisualsFrom(episode, 976, 986, blackStart: 988));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((976, Duration, SegmentSource.KeyframeVisuals), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
    }

    [Fact]
    public async Task DarkLeadInBeforeTheBlackRoll_StaysOutOfTheCardCandidate()
    {
        // 900 to 940 is 90 percent black and low entropy, 942 to 979.5 is the roll, white cards follow to
        // the end. The black-frame transition check starts the roll at 942; the card candidate must not
        // reach back into the lead-in.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            creditsScan: (_, _) => [.. CreateDenseFrames(350, 391.5, 90), .. CreateDenseFrames(392, 429.5, 100)],
            keyframeVisuals: DarkLeadInVisuals);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((942, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
    }

    [Fact]
    public async Task MixedCardRunBeforeASeparateBlackRoll_IsStoredBesideIt()
    {
        // White cards 560 to 570, black cards 572 to 590, content, then the default roll. The
        // black-frame analyzer accepts both black scenes and returns the roll; the earlier scene still
        // lets the mixed run qualify as its own segment.
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(
            creditsScan: (_, _) => [.. CreateDenseFrames(22, 40, 100), .. CreateDenseFrames(400, 449.5, 100)],
            keyframeVisuals: MixedRunVisuals);

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal(
            [(560, 590, SegmentSource.KeyframeVisuals), (BlackStart, Duration, SegmentSource.BlackFrame)],
            segments.Select(s => (s.ToSegment().Start, s.ToSegment().End, s.Source)).ToList());
    }

    [Fact]
    public async Task DetectNonBlackCreditsOff_IgnoresCardCredits()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(keyframeVisuals: episode => CardVisualsFrom(episode, BlackStart - 60, Duration));
        var config = new PluginConfiguration { DetectNonBlackCredits = false };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(0, ffmpeg.VisualScanCalls);
        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((BlackStart, Duration, SegmentSource.BlackFrame), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
    }

    [Fact]
    public async Task BlackFrameAction_KeepsTheCardCandidate()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(keyframeVisuals: episode => CardVisualsFrom(episode, BlackStart - 60, Duration));

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal((BlackStart - 60, Duration, SegmentSource.Combined), (segment.ToSegment().Start, segment.ToSegment().End, segment.Source));
    }

    [Fact]
    public async Task LegacyBlackFrameAnalyzer_FeedsThePassUnderItsToggle()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason(
            rangeScan: (_, range, _, _, _) => range.Start is >= BlackStart and < Duration ? [new BlackFrame(95, 0, 0)] : [],
            keyframeVisuals: episode => CardVisualsFrom(episode, BlackStart - 60, Duration));
        var config = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, UseChapterMarkersBlackFrame = false };

        await CreatePass(ffmpeg, database, config: config).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(0, ffmpeg.VisualScanCalls);
        var segments = (await database.GetSegmentsAsync(episodes[0].EpisodeId)).OrderBy(s => s.StartTicks).ToList();
        Assert.Equal([SegmentSource.Chapter, SegmentSource.BlackFrame], segments.Select(s => s.Source).ToList());
        Assert.Equal((900, 950), (segments[0].ToSegment().Start, segments[0].ToSegment().End));
        Assert.InRange(segments[1].ToSegment().Start, BlackStart, BlackStart + 10); // legacy binary search precision
        Assert.Equal(Duration, segments[1].ToSegment().End);
    }

    [Fact]
    public async Task NoCandidates_SettlesTheEpisodeWithoutSegments()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(blackFrames: false);

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
        var (episodes, ffmpeg, database) = CreateSeason(blackFrames: false, sharedAudioLastPoint: SharedAudioFirstPoint - 1);
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

    private static double PointTime(int point) => WindowStart + (point * ChromaprintConstants.SampleDuration);

    private static IDisposable Scope(params ChapterInfo[] chapters)
        => EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration(), chapters);

    private static ChapterInfo Chapter(string name, double start)
        => new() { Name = name, StartPositionTicks = TimeSpan.FromSeconds(start).Ticks };

    private static CreditsPass CreatePass(StubFFmpegService ffmpeg, IIntroSkipperDatabase database, DetectionCacheService? cache = null, PluginConfiguration? config = null)
        => new(NullLoggerFactory.Instance, ffmpeg, cache ?? DatabaseTestHelpers.CreateTempCacheService(), database, config ?? new PluginConfiguration());

    /// <summary>
    /// A two-episode season over the stub. Black frames sit at 950 to 999.5 in file time and the
    /// scan reports them relative to the credits window start; silence and keyframe lookups
    /// return nothing so end adjustment leaves ends alone.
    /// </summary>
    private static (List<QueuedEpisode> Episodes, StubFFmpegService Ffmpeg, IIntroSkipperDatabase Database) CreateSeason(
        bool blackFrames = true,
        double blackStart = BlackStart,
        double blackEnd = Duration - 0.5,
        int sharedAudioLastPoint = DefaultSharedAudioLastPoint,
        Func<QueuedEpisode, Exception?>? fingerprintFailure = null,
        Func<QueuedEpisode, int, BlackFrame[]>? creditsScan = null,
        Func<QueuedEpisode, TimeRange, int, int, AnalysisMode, BlackFrame[]>? rangeScan = null,
        Func<QueuedEpisode, KeyframeVisual[]>? keyframeVisuals = null)
    {
        var seasonId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 2).Select(number => Episode(seasonId, number)).ToList();

        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => fingerprintFailure?.Invoke(episode) is { } failure
                ? throw failure
                : SharedAudioFingerprint(episode, sharedAudioLastPoint),
            CreditsBlackFrames = creditsScan ?? ((episode, _) => blackFrames ? BlackFramesFrom(episode, blackStart, blackEnd) : []),
            KeyframeVisuals = keyframeVisuals ?? (_ => []),
            RangeBlackFrames = rangeScan ?? ((_, _, _, _, _) => []),
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

    private static BlackFrame[] BlackFramesFrom(QueuedEpisode episode, double blackStart = BlackStart, double blackEnd = Duration - 0.5)
        => CreateDenseFrames(Math.Max(0, blackStart - episode.CreditsFingerprintStart), blackEnd - episode.CreditsFingerprintStart, 95);

    private static KeyframeVisual[] MixedRunVisuals(QueuedEpisode episode)
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= episode.Duration - episode.CreditsFingerprintStart; time += 2)
        {
            var fileTime = time + episode.CreditsFingerprintStart;
            visuals.Add(
                fileTime is >= 560 and <= 570 ? new KeyframeVisual(time, 0.12, 30)
                : fileTime is >= 572 and <= 590 || fileTime is >= BlackStart and <= Duration - 0.5 ? new KeyframeVisual(time, 0.0, 0.0)
                : new KeyframeVisual(time, 0.55, 108));
        }

        return [.. visuals];
    }

    private static KeyframeVisual[] DarkLeadInVisuals(QueuedEpisode episode)
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= episode.Duration - episode.CreditsFingerprintStart; time += 2)
        {
            var fileTime = time + episode.CreditsFingerprintStart;
            visuals.Add(
                fileTime is >= 900 and <= 979.5 ? new KeyframeVisual(time, 0.1, 0)
                : fileTime >= 980 ? new KeyframeVisual(time, 0.12, 30)
                : new KeyframeVisual(time, 0.55, 108));
        }

        return [.. visuals];
    }

    /// <summary>
    /// Keyframe visuals every two seconds across the credits window: busy content outside the card
    /// range, a uniform card inside it, black where the default roll's black frames sit, relative to
    /// the credits window start like the scan reports them.
    /// </summary>
    private static KeyframeVisual[] CardVisualsFrom(QueuedEpisode episode, double cardStart, double cardEnd, bool blackRoll = true, double blackStart = BlackStart)
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= episode.Duration - episode.CreditsFingerprintStart; time += 2)
        {
            var fileTime = time + episode.CreditsFingerprintStart;
            visuals.Add(
                blackRoll && fileTime >= blackStart && fileTime <= Duration - 0.5 ? new KeyframeVisual(time, 0.0, 0.0)
                : fileTime >= cardStart && fileTime <= cardEnd ? new KeyframeVisual(time, 0.12, 30)
                : new KeyframeVisual(time, 0.55, 108));
        }

        return [.. visuals];
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
