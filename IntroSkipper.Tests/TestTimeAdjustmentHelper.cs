// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Minimal IFFmpegService stub for unit testing.
/// Returns pre-configured silence and keyframe data without invoking FFmpeg.
/// </summary>
internal sealed class FakeFFmpegService : IFFmpegService
{
    private readonly TimeRange[] _silence;
    private readonly double[] _keyframes;

    internal FakeFFmpegService(TimeRange[] silence, double[] keyframes)
    {
        _silence = silence;
        _keyframes = keyframes;
    }

    public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<uint>());

    public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(_silence);

    public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, TimeRange range, int minimum, int threshold, AnalysisMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<BlackFrame>());

    public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<BlackFrame>());

    public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(_keyframes);

    public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.FromResult<double?>(null);

    public string GetChromaprintLogs() => string.Empty;
}

public class TestTimeAdjustmentHelper
{
    private static (TimeAdjustmentHelper helper, PluginConfiguration cfg) CreateHelper(PluginConfiguration? cfg = null, AnalysisMode mode = AnalysisMode.Introduction)
    {
        cfg ??= new PluginConfiguration
        {
            EndSnapThreshold = 2.0,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            AdjustWindowInward = 2.0,
            AdjustWindowOutward = 2.0,
            IntroStartOffset = 0,
            IntroEndOffset = 0,
        };

        return (new TimeAdjustmentHelper(NullLogger.Instance, cfg, mode, null!), cfg);
    }

    private static (TimeAdjustmentHelper helper, PluginConfiguration cfg) CreateHelperWithFfmpeg(
        FakeFFmpegService ffmpeg,
        PluginConfiguration? cfg = null,
        AnalysisMode mode = AnalysisMode.Introduction)
    {
        cfg ??= new PluginConfiguration
        {
            EndSnapThreshold = 2.0,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = true,
            SnapToKeyframe = true,
            AdjustWindowInward = 5.0,
            AdjustWindowOutward = 2.0,
            IntroStartOffset = 0,
            IntroEndOffset = 0,
            SilenceDetectionMinimumDuration = 0.1,
        };

        return (new TimeAdjustmentHelper(NullLogger.Instance, cfg, mode, ffmpeg), cfg);
    }

    [Fact]
    public async Task StartOffset_IsIgnored_When_SnappingToEpisodeStart()
    {
        var (helper, cfg) = CreateHelper();
        cfg.IntroStartOffset = 2; // user-configured offset

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 60 };
        var original = new Segment(episode.EpisodeId) { Start = 1.2, End = 10 };

        var adjusted = await helper.AdjustIntroTimesAsync(episode, original);

        Assert.Equal(0, adjusted.Start);
        Assert.Equal(10, adjusted.End);
    }

    [Fact]
    public async Task StartOffset_IsApplied_When_NotSnapping()
    {
        var (helper, cfg) = CreateHelper();
        cfg.IntroStartOffset = 2;

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 60 };
        var original = new Segment(episode.EpisodeId) { Start = 5, End = 12 };

        var adjusted = await helper.AdjustIntroTimesAsync(episode, original);

        Assert.Equal(7, adjusted.Start);
        Assert.Equal(12, adjusted.End);
    }

    [Fact]
    public async Task Start_And_End_Are_Clamped_To_Duration()
    {
        var (helper, cfg) = CreateHelper();
        cfg.IntroStartOffset = 0;
        cfg.IntroEndOffset = 100; // will try to push end negative

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 30 };
        var original = new Segment(episode.EpisodeId) { Start = -5, End = 200 };

        var adjusted = await helper.AdjustIntroTimesAsync(episode, original);

        Assert.Equal(0, adjusted.Start); // clamped from -5 to 0 and snapped
        Assert.Equal(30, adjusted.End);  // clamped from 200 to duration before end logic kicks in
    }

    /// <summary>
    /// When both AdjustIntroBasedOnSilence and SnapToKeyframe are true, the silence
    /// candidate that aligns with a keyframe should win over a longer silence that
    /// doesn't — because the keyframe proximity bonus (0.6) outweighs the duration
    /// weight (0.4) at typical durations.
    /// </summary>
    [Fact]
    public async Task KeyframeWeighting_PrefersKeyframeAlignedSilence_OverLongerNonAlignedSilence()
    {
        // Scenario:
        //   silenceA: starts at 22.0 s, duration 2.0 s  → no keyframe nearby → score = 2.0*0.4 + 0.0*0.6 = 0.80
        //   silenceB: starts at 25.0 s, duration 1.0 s  → keyframe at 25.1 s  → score = 1.0*0.4 + 1.0*0.6 = 1.00
        //   Expected result: silenceB.Start = 25.0 (higher score wins)

        var searchWindowStart = 20.0;
        var currentEnd = 27.0;
        var duration = 60.0;

        var silenceA = new TimeRange(22.0, 24.0); // duration 2.0
        var silenceB = new TimeRange(25.0, 26.0); // duration 1.0

        var keyframeAt25 = new double[] { 20.5, 25.1, 30.0 };

        var ffmpeg = new FakeFFmpegService(
            silence: [silenceA, silenceB],
            keyframes: keyframeAt25);

        var cfg = new PluginConfiguration
        {
            EndSnapThreshold = 2.0,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = true,
            SnapToKeyframe = true,
            AdjustWindowInward = currentEnd - searchWindowStart, // 7.0 → range starts at 20.0
            AdjustWindowOutward = 2.0,
            IntroStartOffset = 0,
            IntroEndOffset = 0,
            SilenceDetectionMinimumDuration = 0.1,
        };

        var (helper, _) = CreateHelperWithFfmpeg(ffmpeg, cfg);

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = duration };
        var original = new Segment(episode.EpisodeId) { Start = 5.0, End = currentEnd };

        var adjusted = await helper.AdjustIntroTimesAsync(episode, original);

        // silenceB has higher score (1.00 > 0.80) because a keyframe is near its start
        Assert.Equal(25.0, adjusted.End);
    }

    /// <summary>
    /// When AdjustIntroBasedOnSilence=true but SnapToKeyframe=false, the existing
    /// nearest-silence behavior is preserved (first qualifying silence wins).
    /// </summary>
    [Fact]
    public async Task SilenceOnlyPath_PicksFirstQualifyingSilence_WhenSnapToKeyframeIsFalse()
    {
        var currentEnd = 27.0;
        var duration = 60.0;

        var silenceA = new TimeRange(22.0, 24.0); // first qualifying → should be picked
        var silenceB = new TimeRange(25.0, 26.0);

        var ffmpeg = new FakeFFmpegService(
            silence: [silenceA, silenceB],
            keyframes: [25.1]);

        var cfg = new PluginConfiguration
        {
            EndSnapThreshold = 2.0,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = true,
            SnapToKeyframe = false,
            AdjustWindowInward = 7.0,
            AdjustWindowOutward = 2.0,
            IntroStartOffset = 0,
            IntroEndOffset = 0,
            SilenceDetectionMinimumDuration = 0.1,
        };

        var (helper, _) = CreateHelperWithFfmpeg(ffmpeg, cfg);

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = duration };
        var original = new Segment(episode.EpisodeId) { Start = 5.0, End = currentEnd };

        var adjusted = await helper.AdjustIntroTimesAsync(episode, original);

        // Original silence-only logic: first qualifying silence start is returned
        Assert.Equal(22.0, adjusted.End);
    }
}
