// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
/// </remarks>
public class DbSeasonInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    /// <param name="episodeIds">Episode IDs.</param>
    public DbSeasonInfo(Guid seasonId, AnalysisMode mode, AnalyzerAction action, IEnumerable<Guid>? episodeIds = null)
        : this(seasonId, mode, action, episodeIds, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    /// <param name="episodeIds">Episode IDs.</param>
    /// <param name="configHash">Configuration hash used when the episode ID set was last analyzed.</param>
    public DbSeasonInfo(Guid seasonId, AnalysisMode mode, AnalyzerAction action, IEnumerable<Guid>? episodeIds, string configHash)
    {
        SeasonId = seasonId;
        Type = mode;
        Action = action;
        EpisodeIds = episodeIds ?? [];
        ConfigHash = configHash;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
    /// </summary>
    public DbSeasonInfo()
    {
    }

    /// <summary>
    /// Gets the item ID.
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
    /// Gets the season number.
    /// </summary>
    public IEnumerable<Guid> EpisodeIds { get; private set; } = [];

    /// <summary>
    /// Gets the configuration hash used when the episode ID set was last analyzed.
    /// </summary>
    public string ConfigHash { get; private set; } = string.Empty;
}
