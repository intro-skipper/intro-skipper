// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Updates the optional analysis-window overrides for a season.
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
}
