// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Stores analysis overrides for one season key. A null value inherits the corresponding
/// plugin-wide setting, except the post-credits preview choice, which is nullable only for
/// legacy rows awaiting migration and defaults to disabled for new seasons.
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
    /// Gets or sets a value indicating whether to derive a preview from the end of credits.
    /// Null is retained only for legacy rows that have not yet been migrated.
    /// </summary>
    public bool? PreviewFromCreditsEnd { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether analysis found recurring short tails after Credits in this season.
    /// </summary>
    public bool PreviewFromCreditsEndRecommended { get; set; }

    /// <summary>
    /// Gets or sets the optional setting that enables subtitle-based recap detection.
    /// </summary>
    public bool? SubtitleRecapDetection { get; set; }

    /// <summary>
    /// Gets or sets the optional setting that enables subtitle-based preview detection.
    /// </summary>
    public bool? SubtitlePreviewDetection { get; set; }
}
