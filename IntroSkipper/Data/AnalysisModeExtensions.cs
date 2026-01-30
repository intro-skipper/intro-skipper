// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;

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

/// <summary>
/// Extension methods for <see cref="AnalysisMode"/>.
/// </summary>
public static class AnalysisModeExtensions
{
    /// <summary>
    /// Gets the total count of analysis modes.
    /// </summary>
    /// <returns>The count of analysis modes.</returns>
    public static int GetModeCount() => Enum.GetValues<AnalysisMode>().Length;

    /// <summary>
    /// Gets the index of the specified analysis mode.
    /// </summary>
    /// <param name="mode">The analysis mode.</param>
    /// <returns>The index of the mode.</returns>
    public static int GetIndex(this AnalysisMode mode) => (int)mode;

    /// <summary>
    /// Gets the analysis mode from the specified index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns>The analysis mode.</returns>
    public static AnalysisMode FromIndex(int index) => (AnalysisMode)index;

    /// <summary>
    /// Gets all analysis modes.
    /// </summary>
    /// <returns>Array of all analysis modes.</returns>
    public static AnalysisMode[] GetAllModes() => Enum.GetValues<AnalysisMode>();
}
