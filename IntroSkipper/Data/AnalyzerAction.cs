// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Type of media file analysis to perform.
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
