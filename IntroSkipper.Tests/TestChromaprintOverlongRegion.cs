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
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// A season whose episodes really do share an opening longer than
/// <see cref="PluginConfiguration.MaximumIntroDuration"/> produces no segment at all. That is the
/// configured guard working, but it has to be visible in the log: the pair correlated, so the
/// "unable to find a shared introduction sequence" trace never fires and the season is otherwise
/// indistinguishable from one whose audio never matched.
/// </summary>
/// <remarks>
/// The fixture reproduces the shape measured on a real library: two episodes of a season whose
/// Chromaprints match at shift 0 over an unbroken 1016-sample prefix and diverge completely
/// afterwards. The configuration is that installation's (all stock defaults for these fields).
/// </remarks>
public sealed class TestChromaprintOverlongRegion
{
    // The measured shared prefix: 1016 consecutive samples, zero gaps, at shift 0.
    private const int SharedSamples = 1016;

    // Fingerprint lengths of the two measured episodes (575.1 s and 597.5 s windows).
    private const int LhsLength = 4644;
    private const int RhsLength = 4825;

    [Fact]
    public async Task AnalyzeMediaFiles_LogsTheDiscardedRegion_WhenTheSharedOpeningExceedsTheMaximum()
    {
        // FindContiguous reports the run as a sample-position span, so a 1016-sample run is
        // 1015 sample durations long: 1015 * (4096 / 11025 / 3) s = 125.697 s. That is past the
        // 120 s maximum, so every pair in the season is discarded.
        var expectedDuration = (SharedSamples - 1) * ChromaprintConstants.SampleDuration;
        Assert.True(expectedDuration > 120, "fixture must exceed the configured maximum");

        var fingerprints = new Dictionary<string, uint[]>
        {
            ["A"] = Fingerprint(LhsLength, 0xFFFF0000u),
            ["B"] = Fingerprint(RhsLength, 0x0000FFFFu),
        };
        var episodes = fingerprints.Keys.Select((name, index) => new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = name,
            EpisodeNumber = index + 1,
            Duration = 1800,
            IntroFingerprintEnd = 600,
        }).ToList();
        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => fingerprints[episode.Name],
        };
        var logger = new DiscardLogger();
        using var db = new TempSegmentDb();
        var analyzer = new ChromaprintAnalyzer(
            logger,
            ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(),
            db.Database,
            new PluginConfiguration
            {
                MinimumIntroDuration = 15,
                MaximumIntroDuration = 120,
                MaximumFingerprintPointDifferences = 6,
                MaximumTimeSkip = 3.5,
                InvertedIndexShift = 2,

                // Off so that raising the maximum leaves a persisted segment rather than an
                // ffmpeg call the stub cannot serve: the "no segment" assertions below have to
                // fail on the segment, not on the harness.
                AdjustIntroBasedOnChapters = false,
                AdjustIntroBasedOnSilence = false,
                SnapToKeyframe = false,
            });

        // The region really is found - only the maximum rejects it.
        var (lhs, rhs) = analyzer.CompareEpisodes(
            episodes[0].EpisodeId,
            fingerprints["A"],
            episodes[1].EpisodeId,
            fingerprints["B"]);
        Assert.Equal(0, lhs.Start);
        Assert.Equal(expectedDuration, lhs.End);
        Assert.Equal(expectedDuration, rhs.End);

        await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        // The observed symptom: the whole season is left without a segment.
        foreach (var episode in episodes)
        {
            Assert.Empty(await db.Database.GetSegmentsAsync(episode.EpisodeId));
            Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        }

        // ...and the log says why, naming both the region it threw away and the limit that did it.
        var message = Assert.Single(logger.Messages);
        Assert.Contains("125.70s", message, StringComparison.Ordinal);
        Assert.Contains("maximum of 120s", message, StringComparison.Ordinal);
        Assert.Contains(episodes[0].EpisodeId.ToString(), message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(episodes[1].EpisodeId.ToString(), message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A fingerprint that opens with the shared run and then diverges. The tail is
    /// <paramref name="tailBase"/> XOR the position, so two episodes' tails differ in 32 minus at
    /// most 11 bits at every alignment - never within the 6-bit similarity tolerance - and the
    /// shared prefix is the only region either episode can match.
    /// </summary>
    private static uint[] Fingerprint(int length, uint tailBase) =>
        [.. Enumerable.Range(0, length).Select(i => i < SharedSamples ? 0x20000000u + (uint)i : tailBase ^ (uint)i)];

    private sealed class DiscardLogger : ILogger<ChromaprintAnalyzer>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug && eventId.Name == "LogSharedRegionTooLong")
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
