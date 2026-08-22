// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Stores the per-season state of one analysis mode: the analyzer action override and
/// the episode set present when the settled-season reanalysis last completed. Which
/// items were analyzed, and under which configuration, is recorded per item in
/// <see cref="DbAnalyzedItem"/>.
/// </summary>
public class DbSeasonState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonState"/> class.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    /// <param name="settledReanalysisEpisodeIds">Episode IDs present when the settled-season reanalysis completed.</param>
    public DbSeasonState(Guid seasonId, AnalysisMode mode, AnalyzerAction action, IEnumerable<Guid>? settledReanalysisEpisodeIds = null)
    {
        SeasonId = seasonId;
        Type = mode;
        Action = action;

        // Materialize eagerly: EF Core tracks this IEnumerable<Guid> property as a
        // primitive collection, which only accepts arrays or IList<Guid> instances.
        SettledReanalysisEpisodeIds = settledReanalysisEpisodeIds is null ? [] : [.. settledReanalysisEpisodeIds];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonState"/> class.
    /// </summary>
    public DbSeasonState()
    {
    }

    /// <summary>
    /// Gets the season ID.
    /// </summary>
    public Guid SeasonId { get; private set; }

    /// <summary>
    /// Gets the analysis mode.
    /// </summary>
    public AnalysisMode Type { get; private set; }

    /// <summary>
    /// Gets the analyzer action.
    /// </summary>
    public AnalyzerAction Action { get; private set; }

    /// <summary>
    /// Gets the episode IDs present when the settled-season reanalysis completed.
    /// </summary>
    public IEnumerable<Guid> SettledReanalysisEpisodeIds { get; private set; } = [];
}
