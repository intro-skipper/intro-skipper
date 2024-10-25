// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

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
}
