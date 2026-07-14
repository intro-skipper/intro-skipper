// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Stores the current per-season state for one analysis mode.
/// </summary>
public class DbSeasonState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonState"/> class.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    /// <param name="episodeIds">Episode IDs analyzed with the current configuration.</param>
    public DbSeasonState(Guid seasonId, AnalysisMode mode, AnalyzerAction action, IEnumerable<Guid>? episodeIds = null)
        : this(seasonId, mode, action, episodeIds, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonState"/> class.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    /// <param name="episodeIds">Episode IDs analyzed with the current configuration.</param>
    /// <param name="configHash">Configuration hash used when the episode ID set was last analyzed.</param>
    /// <param name="settledReanalysisEpisodeIds">Episode IDs present when the settled-season reanalysis completed.</param>
    public DbSeasonState(
        Guid seasonId,
        AnalysisMode mode,
        AnalyzerAction action,
        IEnumerable<Guid>? episodeIds,
        string configHash,
        IEnumerable<Guid>? settledReanalysisEpisodeIds = null)
    {
        SeasonId = seasonId;
        Type = mode;
        Action = action;
        EpisodeIds = episodeIds ?? [];
        ConfigHash = configHash;
        SettledReanalysisEpisodeIds = settledReanalysisEpisodeIds ?? [];
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
    /// Gets the episode IDs analyzed with the current configuration.
    /// </summary>
    public IEnumerable<Guid> EpisodeIds { get; private set; } = [];

    /// <summary>
    /// Gets the configuration hash used when the episode ID set was last analyzed.
    /// </summary>
    public string ConfigHash { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the episode IDs present when the settled-season reanalysis completed.
    /// </summary>
    public IEnumerable<Guid> SettledReanalysisEpisodeIds { get; private set; } = [];

    internal static string SerializeEpisodeIds(IEnumerable<Guid> episodeIds)
        => JsonSerializer.Serialize(episodeIds, (JsonSerializerOptions?)null);
}
