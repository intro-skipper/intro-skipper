// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// Season-level rules that decide whether analysis runs at all. Shared so the analyzer, the
/// settled-season planner, and the queue reporting cannot drift apart on what counts as analyzable.
/// </summary>
internal static class AnalysisEligibility
{
    /// <summary>
    /// Determines whether a whole season is skipped because it holds specials and the season-zero
    /// opt-in is off. Movies carry season number 0 as well, so they are excluded from the rule.
    /// </summary>
    /// <param name="first">Any episode from the season; only its category and season number are read.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns><see langword="true"/> when analysis skips the season entirely.</returns>
    internal static bool IsSeasonZeroOptedOut(QueuedEpisode first, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(config);

        return first.Category is not QueuedMediaCategory.Movie
            && first.SeasonNumber == 0
            && !config.AnalyzeSeasonZero;
    }
}
