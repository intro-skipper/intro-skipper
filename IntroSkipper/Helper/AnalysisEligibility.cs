// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// Shared season-level rules that decide whether analysis runs at all.
/// </summary>
internal static class AnalysisEligibility
{
    /// <summary>
    /// Determines whether specials are opted out of analysis. Movies also use season number zero,
    /// but are not specials and must remain eligible.
    /// </summary>
    internal static bool IsSeasonZeroOptedOut(QueuedEpisode first, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(config);

        return first.Category is not QueuedMediaCategory.Movie
            && first.SeasonNumber == 0
            && !config.AnalyzeSeasonZero;
    }
}
