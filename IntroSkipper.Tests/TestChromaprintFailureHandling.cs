// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestChromaprintFailureHandling
{
    [Fact]
    public async Task AnalyzeMediaFiles_FingerprintException_MarksFailureAndContinues()
    {
        var episodes = new[] { CreateEpisode(1), CreateEpisode(2) };
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            new ThrowingFingerprintService(),
            new NoOpDetectionCacheService());

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());

        var result = await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        // A fingerprint that could not be produced is a failed attempt, not a "nothing found" verdict.
        // Left as NotAnalyzed the episode is persisted in the season's analyzed-episode list and cached
        // as NoSegments on the next run, so a one-off ffmpeg failure becomes permanent.
        Assert.Same(episodes, result);
        Assert.All(episodes, e => Assert.Equal(EpisodeState.AnalysisFailed, e.GetAnalyzed(AnalysisMode.Introduction)));
    }

    private static QueuedEpisode CreateEpisode(int number) => new()
    {
        EpisodeId = Guid.NewGuid(),
        SeasonId = Guid.Empty,
        SeriesName = "Rick and Morty",
        SeasonNumber = 1,
        EpisodeNumber = number,
        Name = $"S01E0{number}",
        Duration = 1800,
        IntroFingerprintEnd = 600,
    };

    private sealed class ThrowingFingerprintService : IFFmpegService
    {
        public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new FingerprintException("chromaprint output was malformed");

        public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, TimeRange range, int minimum, int threshold, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;
    }

    private sealed class NoOpDetectionCacheService : IDetectionCacheService
    {
        public bool IsEnabled => false;

        public bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode) => false;

        public bool TryRead<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, out T[] result)
        {
            result = [];
            return false;
        }

        public bool Write<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, T[] items) => false;

        public bool DeleteForItem(Guid itemId) => true;

        public void DeleteByMode(AnalysisMode mode)
        {
        }
    }
}
