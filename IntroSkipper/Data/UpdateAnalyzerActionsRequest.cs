// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Update analyzer actions request.
/// </summary>
public sealed record UpdateAnalyzerActionsRequest
{
    /// <summary>
    /// Gets the season ID.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Gets the analyzer actions.
    /// </summary>
    public IReadOnlyDictionary<AnalysisMode, AnalyzerAction> AnalyzerActions { get; init; } = new Dictionary<AnalysisMode, AnalyzerAction>();
}
