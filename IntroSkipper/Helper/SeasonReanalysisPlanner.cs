// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// Decides whether a season has stopped receiving new episodes and which of its modes a
/// settled-season reanalysis should reset. Pure logic with no I/O.
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
    /// <returns><see langword="true"/> when the season is eligible for a settle re-analysis; otherwise <see langword="false"/>.</returns>
    internal static bool IsSettledForReanalysis(
        IReadOnlyList<QueuedEpisode> seasonEpisodes,
        PluginConfiguration config,
        DateTime utcNow)
    {
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
        if (AnalysisEligibility.IsSeasonZeroOptedOut(first, config))
        {
            return false;
        }

        var newest = seasonEpisodes.Max(e => e.DateAdded);
        return utcNow - newest >= TimeSpan.FromHours(config.SettledSeasonDelayHours);
    }

    /// <summary>
    /// Selects the modes a settled season still needs re-analyzed: those whose analyzer can run,
    /// whose action is not <see cref="AnalyzerAction.None"/>, and whose recorded episode set differs
    /// from the current one (see <see cref="ShouldSettleReanalyze"/>).
    /// </summary>
    /// <param name="settleReanalysisStates">Per-mode season state, keyed by mode; absent modes were never recorded.</param>
    /// <param name="episodeIds">Current episode IDs in the season.</param>
    /// <param name="modes">Modes enabled for this run.</param>
    /// <param name="ffmpegValid">Whether FFmpeg supports Chromaprint.</param>
    /// <returns>The modes to reset, in <paramref name="modes"/> order.</returns>
    internal static List<AnalysisMode> GetSettleReanalysisModes(
        IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)> settleReanalysisStates,
        IReadOnlyCollection<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        bool ffmpegValid)
    {
        var resetModes = new List<AnalysisMode>(modes.Count);
        foreach (var mode in modes)
        {
            var stateExists = settleReanalysisStates.TryGetValue(mode, out var state);
            var action = stateExists ? state.Action : AnalyzerAction.Default;
            if (action != AnalyzerAction.None &&
                CanSettleReanalysisRun(mode, action, ffmpegValid) &&
                (!stateExists || ShouldSettleReanalyze(state.SettledReanalysisEpisodeIds, episodeIds)))
            {
                resetModes.Add(mode);
            }
        }

        return resetModes;
    }

    /// <summary>
    /// Adds Preview to a reset that includes Credits when anime previews are derived from the
    /// credits end, so the derived segments are regenerated with their source.
    /// </summary>
    /// <param name="modes">The modes selected for reset.</param>
    /// <param name="animePreviewFromCreditsEnd">The <see cref="PluginConfiguration.AnimePreviewFromCreditsEnd"/> setting.</param>
    /// <returns>The modes to reset, with Preview appended when it is derived from Credits.</returns>
    internal static IReadOnlyCollection<AnalysisMode> ExpandSettledResetModesForDerivedSegments(
        IReadOnlyList<AnalysisMode> modes,
        bool animePreviewFromCreditsEnd)
    {
        if (!animePreviewFromCreditsEnd ||
            !modes.Contains(AnalysisMode.Credits) ||
            modes.Contains(AnalysisMode.Preview))
        {
            return modes;
        }

        return [.. modes, AnalysisMode.Preview];
    }

    /// <summary>
    /// Returns whether a settle re-analysis of the mode would actually run an analyzer: Introduction
    /// needs Chromaprint unless the season is pinned to the chapter analyzer.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">The season's analyzer action for the mode.</param>
    /// <param name="ffmpegValid">Whether FFmpeg supports Chromaprint.</param>
    /// <returns><see langword="true"/> when the mode can be re-analyzed; otherwise <see langword="false"/>.</returns>
    internal static bool CanSettleReanalysisRun(AnalysisMode mode, AnalyzerAction action, bool ffmpegValid)
        => mode != AnalysisMode.Introduction || ffmpegValid || action == AnalyzerAction.Chapter;

    /// <summary>
    /// Returns whether a mode's recorded settle-reanalysis episode set differs from the current one.
    /// The record is written by <c>IIntroSkipperDatabase.RecordSettleReanalysisAsync</c> only after
    /// the reset succeeded, so the decision survives plugin restarts.
    /// </summary>
    /// <param name="settledEpisodeIds">Episode IDs recorded when the season was last settle-reanalyzed for this mode.</param>
    /// <param name="episodeIds">Current episode IDs in the season.</param>
    /// <returns><see langword="true"/> when a re-analysis should be performed; otherwise <see langword="false"/>.</returns>
    internal static bool ShouldSettleReanalyze(
        IReadOnlySet<Guid> settledEpisodeIds,
        IReadOnlyCollection<Guid> episodeIds)
        => settledEpisodeIds.Count != episodeIds.Count || episodeIds.Any(id => !settledEpisodeIds.Contains(id));
}
