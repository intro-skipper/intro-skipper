// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Type of media file analysis to perform.
/// </summary>
public enum AnalysisMode
{
    /// <summary>
    /// Detect introduction sequences.
    /// </summary>
    Introduction = 0,

    /// <summary>
    /// Detect credits.
    /// </summary>
    Credits = 1,

    /// <summary>
    /// Detect previews.
    /// </summary>
    Preview = 2,

    /// <summary>
    /// Detect recaps.
    /// </summary>
    Recap = 3,

    /// <summary>
    /// Detect commercials. Only for Segment editor.
    /// </summary>
    Commercial = 4
}
