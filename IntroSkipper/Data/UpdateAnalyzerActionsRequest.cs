// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Update analyzer actions request.
/// </summary>
public class UpdateAnalyzerActionsRequest
{
    /// <summary>
    /// Gets or sets season ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets analyzer actions.
    /// </summary>
    public IReadOnlyDictionary<AnalysisMode, AnalyzerAction> AnalyzerActions { get; set; } = new Dictionary<AnalysisMode, AnalyzerAction>();
}
