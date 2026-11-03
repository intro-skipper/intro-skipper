// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A continuous interval detected by FFmpeg's blackdetect filter.
/// </summary>
/// <param name="Start">Interval start time in seconds: absolute media time from <c>IFFmpegService.DetectBlackIntervalsAsync</c>, relative to the credits fingerprint start once <c>KeyframeAnalyzer</c> hands it to <c>KeyframeScan</c>.</param>
/// <param name="End">Interval end time in seconds, in the same time base as <paramref name="Start"/>.</param>
public sealed record BlackInterval(double Start, double End)
{
    /// <summary>
    /// The minimum continuous black duration (in seconds) blackdetect must observe to report an interval.
    /// Shared by the blackdetect <c>d=</c> argument and the detection-cache hash so the two cannot drift.
    /// </summary>
    public const double MinimumDetectionDuration = 0.1;
}
