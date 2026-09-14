// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Optional analysis-window overrides for a season.
/// </summary>
/// <param name="AnalysisPercent">Percentage override, or null to inherit the global setting.</param>
/// <param name="AnalysisLengthLimit">Maximum runtime override in minutes, or null to inherit the global setting.</param>
/// <param name="PreviewFromCreditsEnd">Whether to derive a preview after credits, or null to inherit the global setting.</param>
public sealed record AnalysisOverrides(int? AnalysisPercent, int? AnalysisLengthLimit, bool? PreviewFromCreditsEnd);
