// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-season choice of analyzer. In the first-wins chain the named analyzer runs first and
/// the rest still follow. In the credits pass, BlackFrame and Chromaprint make that analyzer
/// the only one consulted, while Chapter changes nothing because the default policy already
/// lets a recognized chapter settle the episode.
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
