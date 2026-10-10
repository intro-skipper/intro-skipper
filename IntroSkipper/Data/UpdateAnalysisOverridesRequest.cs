// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Updates the optional analysis overrides for a season.
/// </summary>
public sealed record UpdateAnalysisOverridesRequest
{
    /// <summary>
    /// Gets the season ID.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Gets the optional percentage override.
    /// </summary>
    public int? AnalysisPercent { get; init; }

    /// <summary>
    /// Gets the optional maximum runtime override in minutes.
    /// </summary>
    public int? AnalysisLengthLimit { get; init; }

    /// <summary>
    /// Gets the optional setting that derives a preview from the end of credits.
    /// </summary>
    public bool? PreviewFromCreditsEnd { get; init; }

    /// <summary>
    /// Gets the optional setting that enables subtitle-based recap detection.
    /// </summary>
    public bool? SubtitleRecapDetection { get; init; }

    /// <summary>
    /// Gets the optional setting that enables subtitle-based preview detection.
    /// </summary>
    public bool? SubtitlePreviewDetection { get; init; }
}
