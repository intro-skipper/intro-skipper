// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Jellyfin.Database.Implementations.Enums;

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
    Commercial,
}

/// <summary>
/// Extension methods for <see cref="AnalysisMode"/>.
/// </summary>
internal static class AnalysisModeExtensions
{
    /// <summary>
    /// Converts the instance to a <see cref="MediaSegmentType"/> value.
    /// </summary>
    /// <param name="mode">The analysis mode.</param>
    /// <returns>The corresponding <see cref="MediaSegmentType"/>.</returns>
    internal static MediaSegmentType ToMediaSegment(this AnalysisMode mode) => mode switch
        {
            AnalysisMode.Introduction => MediaSegmentType.Intro,
            AnalysisMode.Recap => MediaSegmentType.Recap,
            AnalysisMode.Preview => MediaSegmentType.Preview,
            AnalysisMode.Credits => MediaSegmentType.Outro,
            AnalysisMode.Commercial => MediaSegmentType.Commercial,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    /// <summary>
    /// Converts the instance to a <see cref="AnalysisMode"/> value.
    /// </summary>
    /// <param name="type">The analysis mode.</param>
    /// <returns>The corresponding <see cref="AnalysisMode"/>.</returns>
    internal static AnalysisMode ToAnalysisMode(this MediaSegmentType type) => type switch
        {
            MediaSegmentType.Intro => AnalysisMode.Introduction,
            MediaSegmentType.Recap => AnalysisMode.Recap,
            MediaSegmentType.Preview => AnalysisMode.Preview,
            MediaSegmentType.Outro => AnalysisMode.Credits,
            MediaSegmentType.Commercial => AnalysisMode.Commercial,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
}
