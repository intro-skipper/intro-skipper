// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024 TwistedUmbrellaX
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 rlauu
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
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
    Introduction,

    /// <summary>
    /// Detect credits.
    /// </summary>
    Credits,

    /// <summary>
    /// Detect previews.
    /// </summary>
    Preview,

    /// <summary>
    /// Detect recaps.
    /// </summary>
    Recap,

    /// <summary>
    /// Detect commercials. Only for Segment editor.
    /// </summary>
    Commercial
}
