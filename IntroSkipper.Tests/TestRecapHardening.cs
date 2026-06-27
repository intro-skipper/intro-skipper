// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for the hardened recap detection path: cold-open-aware start anchoring, structure-aware
/// montage-end selection, false-positive guards against the opening theme, the deduplicated scan
/// window clamp, and the shared Introduction/Recap fingerprint cache key.
/// </summary>
public class TestRecapHardening
{
    // ---- Bug 1: non-zero start (cold open before recap) ----

    [Fact]
    public void ColdOpenBeforeRecap_AnchorsStartToStingNotZero()
    {
        // Structure: cold open [0,40] | recap montage [40,70] | gap | intro [72,92].
        // Shared "previously on" sting at [40,44]; fade/black frames at the cold-open boundary (40)
        // and the montage end (70).
        var sting = new TimeRange(40, 44);
        var blackFrames = Frames(40, 70);
        var context = Context(maxBoundary: 72, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(40, recap.Start); // NOT 0 — the cold open is preserved.
        Assert.Equal(70, recap.End);
    }

    [Fact]
    public void ColdOpenBeforeRecap_AnchorsToFadeJustBeforeSting()
    {
        // The visual recap (and its fade-in) can begin slightly before the shared audio sting.
        var sting = new TimeRange(40, 44);
        var blackFrames = Frames(39.5, 70);
        var context = Context(maxBoundary: 72, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(39.5, recap.Start);
        Assert.Equal(70, recap.End);
    }

    [Fact]
    public void RecapAtEpisodeStart_NoColdOpen_StartsAtZero()
    {
        // Structure: recap [0,30] | intro [32,52]. Sting opens the episode.
        var sting = new TimeRange(0, 3);
        var blackFrames = Frames(30);
        var context = Context(maxBoundary: 32, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(0, recap.Start);
        Assert.Equal(30, recap.End);
    }

    [Fact]
    public void AllowColdOpenDisabled_PreservesLegacyZeroStart()
    {
        // Same cold-open structure as the first test, but the toggle is off: legacy 0:00 start.
        var sting = new TimeRange(40, 44);
        var blackFrames = Frames(40, 70);
        var context = Context(maxBoundary: 72, introDetected: true, allowColdOpen: false);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(0, recap.Start); // legacy behavior swallows the cold open.
        Assert.Equal(70, recap.End);
    }

    // ---- Bug 2: structure-aware montage-end selection (overshoot / undershoot / no frame) ----

    [Fact]
    public void MontageEnd_PicksEarliestValidFrame_NotLatest_AvoidingOvershoot()
    {
        // A mid-episode scene change (110) sits beyond the true montage end (70). The legacy
        // "latest black frame" logic would overshoot to 110 and swallow episode content.
        var sting = new TimeRange(40, 44);
        var blackFrames = Frames(40, 70, 110);
        var context = Context(maxBoundary: 118, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(40, recap.Start);
        Assert.Equal(70, recap.End); // earliest qualifying frame, not 110.
    }

    [Fact]
    public void MontageEnd_SkipsFramesShorterThanMinimumDuration()
    {
        // A black frame at 10 would yield a 10s recap (< 15s floor); it must be skipped in favour
        // of the real montage end at 30.
        var sting = new TimeRange(0, 3);
        var blackFrames = Frames(10, 30);
        var context = Context(maxBoundary: 40, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(0, recap.Start);
        Assert.Equal(30, recap.End);
    }

    [Fact]
    public void NoBlackFrame_IntroDetected_LongSharedRegion_UsesSharedBodyAsRecap()
    {
        // Shared music bed spanning the montage, no fade detected. With an intro detected the theme
        // is already excluded, so the shared region itself is the recap body.
        var sting = new TimeRange(40, 65);
        var blackFrames = NoFrames();
        var context = Context(maxBoundary: 70, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(40, recap.Start);
        Assert.Equal(65, recap.End);
    }

    [Fact]
    public void NoBlackFrame_IntroNotDetected_Rejects()
    {
        // Without a fade/black-frame boundary AND without a detected intro to exclude the theme, the
        // recap cannot be confidently bounded, so detection refuses to guess.
        var sting = new TimeRange(5, 18);
        var blackFrames = NoFrames();
        var context = Context(maxBoundary: 120, introDetected: false);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.Null(recap);
    }

    // ---- Bug 3: false-positive guard against the opening theme ----

    [Fact]
    public void IntroTheme_NoIntroDetected_LongSharedRegion_Rejected()
    {
        // The earliest shared region is the recurring opening theme [30,55] (25s). With no intro
        // detected the scan window is not clamped, so the length guard rejects it as a theme.
        var sting = new TimeRange(30, 55);
        var blackFrames = Frames(55);
        var context = Context(maxBoundary: 120, introDetected: false);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.Null(recap);
    }

    [Fact]
    public void IntroTheme_IntroDetected_ClampedOutByScanWindow()
    {
        // The shared region coincides with the detected intro [30,55]; the intro-clamped window
        // (MaxBoundary = introStart = 30) makes the candidate fail the fit check.
        var sting = new TimeRange(30, 55);
        var blackFrames = Frames(55);
        var context = Context(maxBoundary: 30, introDetected: true);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.Null(recap);
    }

    [Fact]
    public void ShortStingWithMontageBoundary_NoIntroDetected_StillDetected()
    {
        // A genuinely short sting [42,46] followed by a montage-end fade (70) is corroborated by
        // structure, so it survives even when no intro was detected.
        var sting = new TimeRange(42, 46);
        var blackFrames = Frames(42, 70);
        var context = Context(maxBoundary: 120, introDetected: false);

        var recap = RecapDetectionHelper.BuildChromaprintRecap(Guid.NewGuid(), sting, blackFrames, context);

        Assert.NotNull(recap);
        Assert.Equal(42, recap.Start);
        Assert.Equal(70, recap.End);
    }

    // ---- ResolveRecapStart / SelectMontageEnd unit behavior ----

    [Theory]
    [InlineData(0, true, 0)]    // sting opens episode -> 0
    [InlineData(3, true, 0)]    // within cold-open threshold -> 0
    [InlineData(40, true, 40)]  // cold open present, no nearby frame -> sting start
    [InlineData(40, false, 0)]  // cold open disabled -> legacy 0
    public void ResolveRecapStart_Behaves(double stingStart, bool allowColdOpen, double expected)
    {
        var start = RecapDetectionHelper.ResolveRecapStart(stingStart, NoFrames(), allowColdOpen);
        Assert.Equal(expected, start);
    }

    [Fact]
    public void ResolveRecapStart_IgnoresFrameOutsideLeadInWindow()
    {
        // A black frame 10s before the sting is too far back to be the recap boundary (lead-in is
        // 6s); the sting start is used instead.
        var start = RecapDetectionHelper.ResolveRecapStart(40, Frames(30), allowColdOpen: true);
        Assert.Equal(40, start);
    }

    // ---- Bug 4: deduplicated, pure scan-window clamp ----

    [Theory]
    [InlineData(1800, 120, null, 120)] // detection cap wins
    [InlineData(1800, 120, 70.0, 70)]  // intro start wins
    [InlineData(90, 120, null, 90)]    // short episode duration wins
    [InlineData(1800, 120, 200.0, 120)] // intro later than cap -> cap wins
    public void ComputeMaximumBoundary_ClampsToTightestBound(double duration, int maxDetection, double? introStart, double expected)
    {
        Assert.Equal(expected, RecapDetectionHelper.ComputeMaximumBoundary(duration, maxDetection, introStart));
    }

    // ---- Bug 5: shared Introduction/Recap fingerprint cache key (no duplicate decode) ----

    [Theory]
    [InlineData(AnalysisMode.Recap, AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits, AnalysisMode.Credits)]
    public void GetFingerprintCacheMode_MapsRecapToIntroduction(AnalysisMode mode, AnalysisMode expected)
    {
        Assert.Equal(expected, QueuedEpisode.GetFingerprintCacheMode(mode));
    }

    [Fact]
    public void RecapAndIntroduction_ShareTheSameFingerprintRange()
    {
        var episode = new QueuedEpisode { Duration = 1800, IntroFingerprintEnd = 240 };

        // Identical decode range is the reason the cache entry can be shared.
        Assert.Equal(
            episode.GetFingerprintRange(AnalysisMode.Introduction),
            episode.GetFingerprintRange(AnalysisMode.Recap));
    }

    [Fact]
    public async Task FingerprintAsync_Recap_ReusesIntroductionCacheEntry_NoDecode()
    {
        var cache = new RecordingCacheService();
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/nonexistent/episode.mkv", // would fail to decode if a cache miss fell through
            Duration = 1800,
            IntroFingerprintEnd = 240,
        };

        uint[] introFingerprint = [1, 2, 3, 4, 5];
        // Seed only the Introduction-keyed entry (as the Introduction pass would have written).
        cache.Seed(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 240, introFingerprint);

        var ffmpeg = new FFmpegService(NullLogger<FFmpegService>.Instance, cache);

        // The Recap pass must hit the shared Introduction entry instead of decoding again.
        var recapFingerprint = await ffmpeg.FingerprintAsync(episode, AnalysisMode.Recap, CancellationToken.None);

        Assert.Equal(introFingerprint, recapFingerprint);
        Assert.Contains((AnalysisMode.Introduction, CacheEntryType.Chromaprint), cache.Reads);
        Assert.DoesNotContain((AnalysisMode.Recap, CacheEntryType.Chromaprint), cache.Reads);
    }

    // ---- Bug 6: every new knob participates in the config hash ----

    [Fact]
    public void RecapHash_ChangesWhenColdOpenToggleChanges()
    {
        var baseline = new PluginConfiguration();
        var toggled = new PluginConfiguration { RecapAllowColdOpen = !baseline.RecapAllowColdOpen };

        Assert.NotEqual(
            ConfigHasher.Analysis(baseline, AnalysisMode.Recap, AnalyzerAction.Default),
            ConfigHasher.Analysis(toggled, AnalysisMode.Recap, AnalyzerAction.Default));
    }

    private static IReadOnlyList<BlackFrame> Frames(params double[] times)
    {
        var frames = new List<BlackFrame>(times.Length);
        for (var i = 0; i < times.Length; i++)
        {
            frames.Add(new BlackFrame(90, times[i], i));
        }

        return frames;
    }

    private static IReadOnlyList<BlackFrame> NoFrames() => [];

    private static RecapDetectionHelper.RecapBuildContext Context(
        double maxBoundary,
        bool introDetected,
        bool allowColdOpen = true,
        int minimumRecapDuration = 15,
        int maximumRecapDuration = 120,
        int minimumRecapDetectionDuration = 15)
        => new(
            maxBoundary,
            introDetected,
            allowColdOpen,
            minimumRecapDuration,
            maximumRecapDuration,
            minimumRecapDetectionDuration);

    private sealed class RecordingCacheService : IDetectionCacheService
    {
        private readonly Dictionary<(Guid Id, AnalysisMode Mode, CacheEntryType Type, double Start, double End), Array> _store = [];

        public List<(AnalysisMode Mode, CacheEntryType Type)> Reads { get; } = [];

        public bool IsEnabled => true;

        public void Seed<T>(Guid id, AnalysisMode mode, CacheEntryType type, double start, double end, T[] items)
            => _store[(id, mode, type, start, end)] = items;

        public bool TryRead<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, out T[] result)
        {
            Reads.Add((mode, type));
            if (_store.TryGetValue((itemId, mode, type, start, end), out var stored) && stored is T[] typed)
            {
                result = typed;
                return true;
            }

            result = [];
            return false;
        }

        public bool Write<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, T[] items)
        {
            _store[(itemId, mode, type, start, end)] = items;
            return true;
        }

        public void DeleteForItem(Guid itemId)
        {
        }

        public void DeleteByMode(AnalysisMode mode)
        {
        }

        public bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode) => false;
    }
}
