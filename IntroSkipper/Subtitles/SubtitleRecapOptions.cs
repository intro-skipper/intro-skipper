// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Subtitles;

/// <summary>
/// Tunables for <see cref="SubtitleRecapSegmentBuilder"/>.
/// </summary>
public sealed record SubtitleRecapOptions
{
    /// <summary>
    /// Gets the latest cue start (in seconds) that may anchor a recap. Cues beyond this are ignored,
    /// which keeps an incidental "previously on" mid-episode from being treated as a recap.
    /// </summary>
    public double MaxWindowSeconds { get; init; } = 150;

    /// <summary>
    /// Gets the maximum gap (in seconds) allowed between consecutive cues while growing the recap
    /// cluster. A larger gap ends the cluster (e.g. the transition into the cold open).
    /// </summary>
    public double MaxClusterGapSeconds { get; init; } = 12;

    /// <summary>
    /// Gets the minimum acceptable recap duration (in seconds). Shorter results are rejected.
    /// </summary>
    public double MinDurationSeconds { get; init; } = 5;

    /// <summary>
    /// Gets the maximum acceptable recap duration (in seconds). The end is clamped to this.
    /// </summary>
    public double MaxDurationSeconds { get; init; } = 120;

    /// <summary>
    /// Gets the forward window (in seconds) within which the cluster end is snapped to a black frame
    /// (the fade-out that typically ends a recap montage). Set to 0 to disable black-frame snapping.
    /// </summary>
    public double BlackFrameSnapSeconds { get; init; } = 6;

    /// <summary>
    /// Gets a value indicating whether the start may be snapped back to 0 when the anchor cue begins
    /// within <see cref="StartSnapSeconds"/> of the file start. Recaps frequently open the episode,
    /// but unlike the current implementation this is opt-in rather than forced.
    /// </summary>
    public bool SnapStartToZero { get; init; }

    /// <summary>
    /// Gets the threshold (in seconds) used by <see cref="SnapStartToZero"/>.
    /// </summary>
    public double StartSnapSeconds { get; init; } = 2.0;
}
