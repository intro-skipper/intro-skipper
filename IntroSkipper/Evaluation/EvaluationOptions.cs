// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// Tunable knobs for an evaluation run.
/// </summary>
internal sealed class EvaluationOptions
{
    /// <summary>
    /// Gets or sets the minimum IoU at which a firing detection counts as a correct match,
    /// in the range [0, 1]. The default (0.5) is a common detection-evaluation convention.
    /// </summary>
    public double IouMatchThreshold { get; set; } = 0.5;
}
