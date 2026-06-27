// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers;

/// <summary>
/// Tunable parameters for a <see cref="CrossEpisodeReuseMatcher"/> pass. Defaults are translated from
/// the shipped <see cref="Configuration.PluginConfiguration"/> Chromaprint tuning so the prototype
/// behaves like production.
/// </summary>
/// <remarks>
/// RESEARCH SPIKE (RFC B) — see <c>docs/recap-research/B-cross-episode.md</c>.
/// </remarks>
public sealed class ReuseMatchOptions
{
    /// <summary>
    /// Gets or sets the maximum number of differing bits (out of 32) for two points to be "equal".
    /// Mirrors <c>PluginConfiguration.MaximumFingerprintPointDifferences</c> (default 6).
    /// </summary>
    public int MaxBitDifferences { get; set; } = 6;

    /// <summary>
    /// Gets or sets the +/- jitter applied when probing the reference index during shift voting.
    /// Mirrors <c>PluginConfiguration.InvertedIndexShift</c> (default 2).
    /// </summary>
    public int IndexShift { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum gap (in points) between two matched positions before a contiguous run
    /// is broken. Mirrors <c>PluginConfiguration.MaximumTimeSkip</c> (3.5 s) converted to points.
    /// </summary>
    public int MaxGapPoints { get; set; } = CrossEpisodeReuseMatcher.SecondsToPoints(3.5);

    /// <summary>
    /// Gets or sets the minimum length (in points) of a single reused clip. Mirrors
    /// <c>ChromaprintAnalyzer.RecapCardMinimumDuration</c> (3 s) converted to points.
    /// </summary>
    public int MinRunPoints { get; set; } = CrossEpisodeReuseMatcher.SecondsToPoints(3.0);

    /// <summary>
    /// Gets or sets the maximum number of candidate shifts that are fully scanned. This is the hard
    /// upper bound on the expensive phase: cost is <c>TopShifts * O(query length)</c>.
    /// </summary>
    public int TopShifts { get; set; } = 16;

    /// <summary>
    /// Gets or sets the maximum gap (in points) between two reused clips that should still be merged
    /// into the same montage (covers hard cuts / short black-frame transitions between clips).
    /// </summary>
    public int MaxMontageGapPoints { get; set; } = CrossEpisodeReuseMatcher.SecondsToPoints(6.0);

    /// <summary>
    /// Gets or sets the minimum distinct-point overlap fraction required before a full search runs.
    /// Used for cheap early-exit on shows that do not reuse footage.
    /// </summary>
    public double PreFilterMinOverlap { get; set; } = 0.02;

    /// <summary>
    /// Gets or sets a cap on how many reference occurrences of a single point value are allowed to vote.
    /// Bounds worst-case voting cost when a point value is pathologically common in the reference.
    /// </summary>
    public int MaxVotesPerPoint { get; set; } = 8;
}
