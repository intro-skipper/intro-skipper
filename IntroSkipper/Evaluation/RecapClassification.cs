// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// Confusion-matrix classification of a single evaluated episode.
/// </summary>
internal enum RecapClassification
{
    /// <summary>
    /// The episode has a recap and the detector localized it correctly (IoU at or above threshold).
    /// </summary>
    TruePositive,

    /// <summary>
    /// The episode has no recap but the detector fired anyway.
    /// </summary>
    FalsePositive,

    /// <summary>
    /// The episode has a recap but the detector missed it (no detection, or IoU below threshold).
    /// </summary>
    FalseNegative,

    /// <summary>
    /// The episode has no recap and the detector correctly stayed silent.
    /// </summary>
    TrueNegative,
}
