// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// Decides whether a season has stopped receiving new episodes and is therefore eligible for a
/// settled-season reanalysis. Pure logic with no I/O so it can be unit tested in isolation.
/// </summary>
internal static class SeasonReanalysisPlanner
{
    /// <summary>
    /// Minimum number of episodes a season must contain before a settle re-analysis is worthwhile.
    /// </summary>
    internal const int MinimumEpisodes = 3;

    /// <summary>
    /// Determines whether the supplied season should be re-analyzed because it has settled.
    /// </summary>
    /// <param name="seasonEpisodes">All queued episodes belonging to the season.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns><c>true</c> when the season is eligible for a settle re-analysis; otherwise <c>false</c>.</returns>
    internal static bool IsSettledForReanalysis(
        IReadOnlyList<QueuedEpisode> seasonEpisodes,
        PluginConfiguration config,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(seasonEpisodes);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.ReanalyzeSettledSeasons || seasonEpisodes.Count < MinimumEpisodes)
        {
            return false;
        }

        var first = seasonEpisodes[0];

        if (first.Category is QueuedMediaCategory.Movie)
        {
            return false;
        }

        // Respect the season-zero (specials) opt-in.
        if (first.SeasonNumber == 0 && !config.AnalyzeSeasonZero)
        {
            return false;
        }

        var newest = seasonEpisodes.Max(e => e.DateAdded);
        return utcNow - newest >= TimeSpan.FromHours(config.SettledSeasonDelayHours);
    }
}
