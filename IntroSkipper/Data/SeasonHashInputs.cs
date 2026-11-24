// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// The season overrides and re-check request an analysis record's hashes take besides the
/// configuration, the mode's action and Chromaprint availability (<c>ConfigHasher.Record</c>).
/// <c>BaseItemAnalyzerTask</c> builds them in one place for the writer and the verifier.
/// </summary>
/// <param name="RecheckRequest">The re-check request the season pass read, empty when none was ever made.</param>
/// <param name="AnalysisPercentOverride">Season percentage override.</param>
/// <param name="AnalysisLengthLimitOverride">Season runtime limit override in minutes.</param>
/// <param name="PreviewFromCreditsEnd">Whether the season derives previews from the credits end. For null, the analysis hash takes the plugin-wide setting.</param>
/// <param name="SubtitleRecapDetectionOverride">Season subtitle recap override.</param>
/// <param name="SubtitlePreviewDetectionOverride">Season subtitle preview override.</param>
internal readonly record struct SeasonHashInputs(
    string RecheckRequest,
    int? AnalysisPercentOverride = null,
    int? AnalysisLengthLimitOverride = null,
    bool? PreviewFromCreditsEnd = null,
    bool? SubtitleRecapDetectionOverride = null,
    bool? SubtitlePreviewDetectionOverride = null);
