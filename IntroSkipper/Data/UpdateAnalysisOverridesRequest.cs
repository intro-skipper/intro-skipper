// SPDX-FileCopyrightText: 2026 AbandonedCart
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
    /// Gets a value indicating whether to derive a preview from the end of credits for this season;
    /// <see langword="null"/> preserves the saved choice for clients using the earlier nullable contract.
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
