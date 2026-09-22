// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// What the lead-in probe measures on one frame.
/// </summary>
/// <param name="Background">The 10th percentile luma: the background level.</param>
/// <param name="ForegroundFraction">The share of pixels at least the lettering contrast above the background.</param>
/// <param name="TransitionsPerBandRow">Horizontal edges of the foreground per row that holds foreground: rows of glyphs have many, a lit object a few.</param>
internal readonly record struct LeadInFrameMeasure(double Background, double ForegroundFraction, double TransitionsPerBandRow);
