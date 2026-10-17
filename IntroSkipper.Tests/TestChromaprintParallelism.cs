// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestChromaprintParallelism
{
    [Fact]
    public async Task ComputeCandidatesAsync_BoundsExtractionAndWaitsForCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var ffmpeg = new BlockingFingerprintService();
        var analysis = CreateAnalyzer(ffmpeg);
        var task = analysis.ComputeCandidatesAsync(Episodes(3), [], AnalysisMode.Introduction, cancellation.Token, fingerprintConcurrency: 2);

        await ffmpeg.WaitForTwoExtractionsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, ffmpeg.MaximumConcurrentExtractions);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, ffmpeg.ActiveExtractions);
    }

    [Fact]
    public async Task ComputeCandidatesAsync_ParallelExtractionPreservesCandidatesAndFailures()
    {
        var episodes = Episodes(3);
        var failureId = episodes[2].EpisodeId;
        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => episode.EpisodeId == failureId
                ? throw new FingerprintException("planned failure")
                : Fingerprint(episode),
        };

        var serial = await CreateAnalyzer(ffmpeg).ComputeCandidatesAsync(episodes, [], AnalysisMode.Introduction, CancellationToken.None);
        var parallel = await CreateAnalyzer(ffmpeg).ComputeCandidatesAsync(episodes, [], AnalysisMode.Introduction, CancellationToken.None, fingerprintConcurrency: 2);

        Assert.NotEmpty(serial.Candidates);
        Assert.Equal(CandidateValues(serial), CandidateValues(parallel));
        Assert.Equal(new[] { failureId }, serial.FingerprintFailures);
        Assert.Equal(serial.FingerprintFailures, parallel.FingerprintFailures);
    }

    private static ChromaprintAnalyzer CreateAnalyzer(IFFmpegService ffmpeg)
        => new(
            NullLogger<ChromaprintAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(),
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            new PluginConfiguration
            {
                MinimumIntroDuration = 1,
                MaximumFingerprintPointDifferences = 0,
                MaximumTimeSkip = 0.2,
                InvertedIndexShift = 0,
            });

    private static List<QueuedEpisode> Episodes(int count)
        => Enumerable.Range(1, count).Select(number => new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            EpisodeNumber = number,
            Duration = 1800,
            IntroFingerprintEnd = 60,
        }).ToList();

    private static uint[] Fingerprint(QueuedEpisode episode)
        => Enumerable.Range(0, 200).Select(point => point < 100
            ? 0x40000000u + ((uint)point * 7919u)
            : (uint)point ^ ((uint)episode.EpisodeNumber * 0x11111111u)).ToArray();

    private static IEnumerable<(Guid Id, double Start, double End)> CandidateValues(ChromaprintCandidates candidates)
        => candidates.Candidates.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value.Start, pair.Value.End));

    private sealed class BlockingFingerprintService : StubFFmpegService
    {
        private readonly TaskCompletionSource _twoExtractionsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeExtractions;
        private int _maximumConcurrentExtractions;
        private int _startedExtractions;

        public int ActiveExtractions => Volatile.Read(ref _activeExtractions);

        public int MaximumConcurrentExtractions => Volatile.Read(ref _maximumConcurrentExtractions);

        public override async Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeExtractions);
            UpdateMaximum(active);
            if (Interlocked.Increment(ref _startedExtractions) == 2)
            {
                _twoExtractionsStarted.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _activeExtractions);
            }
        }

        public Task WaitForTwoExtractionsAsync() => _twoExtractionsStarted.Task;

        private void UpdateMaximum(int active)
        {
            var previous = Volatile.Read(ref _maximumConcurrentExtractions);
            while (active > previous)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrentExtractions, active, previous);
                if (observed == previous)
                {
                    return;
                }

                previous = observed;
            }
        }
    }
}
