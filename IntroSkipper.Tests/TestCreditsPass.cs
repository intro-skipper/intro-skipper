// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// test enables it, from about 748 to 983, so 17 seconds remain after it for a probe.
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

        // The black-frame probe starts at the shared audio's end (983.3) rounded down to the 10 s grid.
        Assert.Equal(980, ffmpeg.LastCreditsScanStart);
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
    public async Task ChromaprintWithinMinimumDurationOfTheEnd_SkipsBlackFrameAndExtends()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason(sharedAudioLastPoint: 3560); // shared audio to about 991 of 1000

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(0, ffmpeg.CreditsScanCalls);
        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Chromaprint, segment.Source);
        Assert.Equal(PointTime(SharedAudioFirstPoint), segment.ToSegment().Start, 0.5);
        Assert.Equal(Duration, segment.ToSegment().End);
    }

    [Fact]
    public async Task BlackFrameAction_RestrictsThePassToBlackFrameCandidates()
    {
        using var scope = Scope();
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.BlackFrame, ffmpegValid: true, CancellationToken.None);

        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(WindowStart, ffmpeg.LastCreditsScanStart);
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
    public async Task ChapterAdjacentToBlackFrame_IsExtendedNeverShortened()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason();

        await CreatePass(ffmpeg, database).RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(950, ffmpeg.LastCreditsScanStart);
        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Combined, segment.Source);
        Assert.Equal(900, segment.ToSegment().Start);
        Assert.Equal(Duration, segment.ToSegment().End);
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

        Assert.Equal(scansBefore + 1, ffmpeg.CreditsScanCalls);
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
    public async Task LegacyBlackFrameAnalyzer_FeedsThePassOverTheFullWindow()
    {
        using var scope = Scope(Chapter("Main", 0), Chapter("Ending", 900), Chapter("Epilogue", 950));
        var (episodes, ffmpeg, database) = CreateSeason(
            rangeScan: (_, range, _, _, _) => range.Start is >= BlackStart and < Duration ? [new BlackFrame(95, 0, 0)] : []);
        var config = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, UseChapterMarkersBlackFrame = false };
        var pass = new CreditsPass(NullLoggerFactory.Instance, ffmpeg, DatabaseTestHelpers.CreateTempCacheService(), database, config);

        await pass.RunAsync(episodes, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        var segment = Assert.Single(await database.GetSegmentsAsync(episodes[0].EpisodeId));
        Assert.Equal(SegmentSource.Combined, segment.Source);
        Assert.Equal(900, segment.ToSegment().Start);
        Assert.Equal(Duration, segment.ToSegment().End);
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

    private static double PointTime(int point) => WindowStart + (point * ChromaprintConstants.SampleDuration);

    private static IDisposable Scope(params ChapterInfo[] chapters)
        => EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration(), chapters);

    private static ChapterInfo Chapter(string name, double start)
        => new() { Name = name, StartPositionTicks = TimeSpan.FromSeconds(start).Ticks };

    private static CreditsPass CreatePass(StubFFmpegService ffmpeg, IIntroSkipperDatabase database, DetectionCacheService? cache = null)
        => new(NullLoggerFactory.Instance, ffmpeg, cache ?? DatabaseTestHelpers.CreateTempCacheService(), database, new PluginConfiguration());

    /// <summary>
    /// A two-episode season over the stub. Black frames sit at 950 to 999.5 in file time and the
    /// scan reports them relative to whatever window start the pass probes from; silence and
    /// keyframe probes return nothing so end adjustment leaves ends alone.
    /// </summary>
    private static (List<QueuedEpisode> Episodes, StubFFmpegService Ffmpeg, IIntroSkipperDatabase Database) CreateSeason(
        bool blackFrames = true,
        int sharedAudioLastPoint = DefaultSharedAudioLastPoint,
        Func<QueuedEpisode, Exception?>? fingerprintFailure = null,
        Func<QueuedEpisode, int, BlackFrame[]>? creditsScan = null,
        Func<QueuedEpisode, TimeRange, int, int, AnalysisMode, BlackFrame[]>? rangeScan = null)
    {
        var seasonId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 2).Select(number => Episode(seasonId, number)).ToList();

        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => fingerprintFailure?.Invoke(episode) is { } failure
                ? throw failure
                : SharedAudioFingerprint(episode, sharedAudioLastPoint),
            CreditsBlackFrames = creditsScan ?? ((episode, _) => blackFrames ? BlackFramesFrom(episode) : []),
            KeyframeVisuals = _ => [],
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

    private static BlackFrame[] BlackFramesFrom(QueuedEpisode probe)
        => CreateDenseFrames(Math.Max(0, BlackStart - probe.CreditsFingerprintStart), Duration - 0.5 - probe.CreditsFingerprintStart, 95);

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
