// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-season choice of local analyzer, used when no authoritative SkipMe match exists.
/// None disables analysis from every source. In the first-wins chain the named analyzer
/// runs first and the rest still follow. In the credits pass, BlackFrame and Chromaprint
/// make that analyzer the only one consulted, while Chapter changes nothing because
/// chapters always take part.
/// </summary>
public enum AnalyzerAction
{
    /// <summary>
    /// Default action.
    /// </summary>
    Default,

    /// <summary>
    /// Detect chapters.
    /// </summary>
    Chapter,

    /// <summary>
    /// Detect chromaprint fingerprints.
    /// </summary>
    Chromaprint,

    /// <summary>
    /// Detect black frames.
    /// </summary>
    BlackFrame,

    /// <summary>
    /// No action.
    /// </summary>
    None,
}
