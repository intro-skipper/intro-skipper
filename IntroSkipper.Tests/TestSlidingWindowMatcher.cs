// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntroSkipper.Tests;

public class TestSlidingWindowMatcher
{
    /// <summary>
    /// Two identical fingerprints must produce a valid match with duration ≥ MinimumIntroDuration.
    /// </summary>
    [Fact]
    public void TestIdenticalFingerprintsMatch()
    {
        var fp = GenerateFingerprint(seed: 42, length: 300);
        var matcher = CreateMatcher();

        var (lhs, rhs) = matcher.FindBestMatch(fp, fp);

        Assert.NotNull(lhs);
        Assert.NotNull(rhs);
        Assert.True(lhs.Duration >= 15.0, $"Expected duration ≥ 15s, got {lhs.Duration}");
    }

    /// <summary>
    /// Two completely independent random fingerprints have on average ~50% bit difference per XOR,
    /// which is far above MaximumFingerprintPointDifferences (6 out of 32 bits).
    /// FindBestMatch must return (null, null).
    /// </summary>
    [Fact]
    public void TestRandomFingerprintsNoMatch()
    {
        var lhs = GenerateRandomFingerprint(seed: 1, length: 500);
        var rhs = GenerateRandomFingerprint(seed: 2, length: 500);
        var matcher = CreateMatcher();

        var (lhsRange, rhsRange) = matcher.FindBestMatch(lhs, rhs);

        Assert.Null(lhsRange);
        Assert.Null(rhsRange);
    }

    /// <summary>
    /// Core regression test for the cold-open scenario:
    /// Episode A has a short cold open (150 pts ≈ 18.6s), Episode B has a long cold open
    /// (480 pts ≈ 59.4s). Both share an identical intro block of 200 pts (≈ 24.8s).
    /// The sliding window must locate the intro at the correct offset in each fingerprint.
    /// </summary>
    [Fact]
    public void TestVaryingColdOpenAlignment()
    {
        // 200 identical intro points shared by both episodes.
        var introPts = GenerateFingerprint(seed: 99, length: 200);

        // Independent cold opens with different lengths.
        var coldOpenA = GenerateRandomFingerprint(seed: 10, length: 150);
        var coldOpenB = GenerateRandomFingerprint(seed: 20, length: 480);

        // Padding after the intro (different content, same length).
        var restA = GenerateRandomFingerprint(seed: 30, length: 300);
        var restB = GenerateRandomFingerprint(seed: 40, length: 300);

        var lhsFp = Concat(coldOpenA, introPts, restA);  // total 650 pts
        var rhsFp = Concat(coldOpenB, introPts, restB);  // total 980 pts

        var matcher = CreateMatcher();
        var (lhsRange, rhsRange) = matcher.FindBestMatch(lhsFp, rhsFp);

        Assert.NotNull(lhsRange);
        Assert.NotNull(rhsRange);

        // Expected starts (in seconds): coldOpenA.Length * SamplesToSeconds and coldOpenB.Length * SamplesToSeconds.
        // Allow ±2 strides of tolerance (default stride ≈ 1.0s).
        const double samplesToSeconds = 0.1238;
        var expectedLhsStart = coldOpenA.Length * samplesToSeconds;
        var expectedRhsStart = coldOpenB.Length * samplesToSeconds;

        Assert.InRange(lhsRange!.Start, expectedLhsStart - 2.0, expectedLhsStart + 2.0);
        Assert.InRange(rhsRange!.Start, expectedRhsStart - 2.0, expectedRhsStart + 2.0);
        Assert.True(lhsRange.Duration >= 15.0, $"LHS duration too short: {lhsRange.Duration}");
        Assert.True(rhsRange.Duration >= 15.0, $"RHS duration too short: {rhsRange.Duration}");
    }

    /// <summary>
    /// Fingerprints shorter than MinimumIntroDuration must return (null, null) without throwing.
    /// </summary>
    [Fact]
    public void TestShortFingerprintReturnsNull()
    {
        // 50 points ≈ 6.2s, below the default MinimumIntroDuration of 15s (121 pts).
        var lhs = GenerateFingerprint(seed: 1, length: 50);
        var rhs = GenerateFingerprint(seed: 2, length: 50);
        var matcher = CreateMatcher();

        var (lhsRange, rhsRange) = matcher.FindBestMatch(lhs, rhs);

        Assert.Null(lhsRange);
        Assert.Null(rhsRange);
    }

    /// <summary>
    /// When the intro is identical at the very beginning of both fingerprints, the first
    /// window comparison (lhsStart=0, rhsStart=0) scores 1.0, triggering an early exit
    /// (EarlyExitScore default 0.9). The resulting range start must snap to 0.
    /// </summary>
    [Fact]
    public void TestEarlyExitAndStartSnapToZero()
    {
        var intro = GenerateFingerprint(seed: 7, length: 200);
        var padding = GenerateRandomFingerprint(seed: 8, length: 600);

        var lhsFp = Concat(intro, padding);
        var rhsFp = Concat(intro, padding);

        var matcher = CreateMatcher();
        var (lhsRange, rhsRange) = matcher.FindBestMatch(lhsFp, rhsFp);

        Assert.NotNull(lhsRange);
        Assert.NotNull(rhsRange);

        // The start-snap-to-zero logic in ChromaprintAnalyzer.CompareEpisodes applies <= 5s;
        // the SlidingWindowMatcher itself doesn't snap – that is done by the caller.
        // Here we verify the raw TimeRange is at or near 0.
        Assert.InRange(lhsRange!.Start, 0.0, 5.0);
        Assert.InRange(rhsRange!.Start, 0.0, 5.0);
        Assert.True(lhsRange.Duration >= 15.0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a deterministic, low-entropy fingerprint where adjacent points differ
    /// by at most 1–2 bits, simulating real Chromaprint continuity.
    /// </summary>
    private static uint[] GenerateFingerprint(int seed, int length)
    {
        var rng = new Random(seed);
        var fp = new uint[length];
        fp[0] = (uint)rng.Next();
        for (var i = 1; i < length; i++)
        {
            // Flip 1 bit to maintain very high similarity between adjacent points.
            fp[i] = fp[i - 1] ^ (1u << rng.Next(0, 3));
        }

        return fp;
    }

    /// <summary>
    /// Generates a fully random fingerprint to simulate unrelated audio.
    /// </summary>
    private static uint[] GenerateRandomFingerprint(int seed, int length)
    {
        var rng = new Random(seed);
        var fp = new uint[length];
        for (var i = 0; i < length; i++)
        {
            fp[i] = (uint)rng.Next();
        }

        return fp;
    }

    private static uint[] Concat(params uint[][] arrays)
    {
        var result = new uint[arrays.Sum(a => a.Length)];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }

    private static SlidingWindowMatcher CreateMatcher()
    {
        var config = new PluginConfiguration();
        var logger = new LoggerFactory().CreateLogger<SlidingWindowMatcher>();
        return new SlidingWindowMatcher(config, logger);
    }
}
