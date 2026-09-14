// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Stores optional analysis-window overrides for one season key. A null value inherits
/// the corresponding plugin-wide setting.
/// </summary>
public sealed class DbSeasonAnalysisOverride
{
    /// <summary>
    /// Gets or sets the season or movie key.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the optional percentage of media to analyze.
    /// </summary>
    public int? AnalysisPercent { get; set; }

    /// <summary>
    /// Gets or sets the optional maximum runtime to analyze, in minutes.
    /// </summary>
    public int? AnalysisLengthLimit { get; set; }

    /// <summary>
    /// Gets or sets the optional setting that derives a preview from the end of credits.
    /// </summary>
    public bool? PreviewFromCreditsEnd { get; set; }
}
