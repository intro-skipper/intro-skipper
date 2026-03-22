// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Finds shared audio segments between two Chromaprint fingerprints using a coarse-to-fine
/// sliding window approach. Used as a fallback when the inverted index approach fails,
/// for example when episodes have cold opens of varying lengths that cause the inverted
/// index to map fingerprint points to incorrect positions.
/// </summary>
public sealed class SlidingWindowMatcher
{
    /// <summary>
    /// Seconds of audio per Chromaprint fingerprint point.
    /// Must match <see cref="ChromaprintAnalyzer"/>'s constant.
    /// </summary>
    private const double SamplesToSeconds = 0.1238;

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SlidingWindowMatcher"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="logger">Logger.</param>
    public SlidingWindowMatcher(PluginConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Finds the best matching pair of windows between two fingerprints by scanning all
    /// possible start positions at a configurable stride, then refining the best candidate
    /// with a full contiguous-range search.
    /// </summary>
    /// <param name="lhsFingerprint">Left-hand-side fingerprint points.</param>
    /// <param name="rhsFingerprint">Right-hand-side fingerprint points.</param>
    /// <returns>
    /// A matched (Lhs, Rhs) <see cref="TimeRange"/> pair if a shared segment of at least
    /// <see cref="PluginConfiguration.MinimumIntroDuration"/> seconds is found;
    /// otherwise <c>(null, null)</c>.
    /// </returns>
    public (TimeRange? Lhs, TimeRange? Rhs) FindBestMatch(uint[] lhsFingerprint, uint[] rhsFingerprint)
    {
        var stride = Math.Max(1, (int)Math.Round(_config.SlidingWindowStepSeconds / SamplesToSeconds));
        var minWindow = Math.Max(1, (int)Math.Round(_config.MinimumIntroDuration / SamplesToSeconds));

        if (lhsFingerprint.Length < minWindow || rhsFingerprint.Length < minWindow)
        {
            return (null, null);
        }

        var bestScore = 0.0;
        var bestLhsStart = -1;
        var bestRhsStart = -1;
        var earlyExit = false;

        var lhsLimit = lhsFingerprint.Length - minWindow;
        var rhsLimit = rhsFingerprint.Length - minWindow;

        for (var lhsStart = 0; lhsStart <= lhsLimit && !earlyExit; lhsStart += stride)
        {
            for (var rhsStart = 0; rhsStart <= rhsLimit; rhsStart += stride)
            {
                var score = ScoreWindow(lhsFingerprint, lhsStart, rhsFingerprint, rhsStart, minWindow);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLhsStart = lhsStart;
                    bestRhsStart = rhsStart;

                    if (score >= _config.SlidingWindowEarlyExitScore)
                    {
                        earlyExit = true;
                        break;
                    }
                }
            }
        }

        if (bestLhsStart < 0)
        {
            return (null, null);
        }

        // Derive the alignment shift and do a full contiguous-range search for precise boundaries.
        var shift = bestRhsStart - bestLhsStart;
        var (lhsRange, rhsRange) = ChromaprintAnalyzer.FindContiguous(
            lhsFingerprint,
            rhsFingerprint,
            shift,
            _config.MaximumFingerprintPointDifferences,
            _config.MaximumTimeSkip,
            _config.MinimumIntroDuration);

        if (lhsRange.End == 0 || rhsRange.End == 0)
        {
            return (null, null);
        }

        return (lhsRange, rhsRange);
    }

    /// <summary>
    /// Computes the similarity score for a window pair: the fraction of fingerprint points
    /// within the window that satisfy the bit-difference threshold.
    /// </summary>
    private double ScoreWindow(uint[] lhs, int lhsStart, uint[] rhs, int rhsStart, int windowLength)
    {
        var upperLimit = Math.Min(windowLength, Math.Min(lhs.Length - lhsStart, rhs.Length - rhsStart));

        if (upperLimit <= 0)
        {
            return 0.0;
        }

        var matches = 0;
        for (var i = 0; i < upperLimit; i++)
        {
            var diff = lhs[lhsStart + i] ^ rhs[rhsStart + i];
            if (ChromaprintAnalyzer.CountBits(diff) <= _config.MaximumFingerprintPointDifferences)
            {
                matches++;
            }
        }

        return (double)matches / upperLimit;
    }
}
