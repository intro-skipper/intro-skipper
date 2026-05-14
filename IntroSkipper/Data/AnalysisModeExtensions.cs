// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Data;

/// <summary>
/// Extension and helper methods for mapping between <see cref="AnalysisMode"/>,
/// Jellyfin <see cref="MediaSegmentType"/>, and segment type strings used by the segment editor API.
/// This is the single source of truth — all production code should use these methods
/// instead of maintaining local dictionaries or switch expressions.
/// </summary>
public static class AnalysisModeExtensions
{
    /// <summary>
    /// Converts an <see cref="AnalysisMode"/> to the corresponding Jellyfin <see cref="MediaSegmentType"/>.
    /// </summary>
    /// <param name="mode">The analysis mode.</param>
    /// <returns>The matching <see cref="MediaSegmentType"/>.</returns>
    /// <exception cref="NotImplementedException">Thrown when the mode has no known mapping.</exception>
    public static MediaSegmentType ToMediaSegmentType(this AnalysisMode mode) =>
        mode.TryToMediaSegmentType(out var type)
            ? type
            : throw new NotImplementedException($"No MediaSegmentType mapping for {mode}");

    /// <summary>
    /// Attempts to convert an <see cref="AnalysisMode"/> to the corresponding <see cref="MediaSegmentType"/>
    /// without throwing an exception for unmapped values.
    /// This is the single source of truth for the AnalysisMode-to-MediaSegmentType mapping.
    /// </summary>
    /// <param name="mode">The analysis mode.</param>
    /// <param name="type">When this method returns <c>true</c>, contains the matching <see cref="MediaSegmentType"/>.</param>
    /// <returns><c>true</c> if the mode has a known mapping; otherwise, <c>false</c>.</returns>
    public static bool TryToMediaSegmentType(this AnalysisMode mode, out MediaSegmentType type)
    {
        switch (mode)
        {
            case AnalysisMode.Introduction:
                type = MediaSegmentType.Intro;
                return true;
            case AnalysisMode.Recap:
                type = MediaSegmentType.Recap;
                return true;
            case AnalysisMode.Preview:
                type = MediaSegmentType.Preview;
                return true;
            case AnalysisMode.Credits:
                type = MediaSegmentType.Outro;
                return true;
            case AnalysisMode.Commercial:
                type = MediaSegmentType.Commercial;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>
    /// Converts a Jellyfin <see cref="MediaSegmentType"/> to the corresponding <see cref="AnalysisMode"/>.
    /// </summary>
    /// <param name="type">The media segment type.</param>
    /// <returns>The matching <see cref="AnalysisMode"/>.</returns>
    /// <exception cref="NotImplementedException">Thrown when the type has no known mapping.</exception>
    public static AnalysisMode ToAnalysisMode(this MediaSegmentType type) => type switch
    {
        MediaSegmentType.Intro => AnalysisMode.Introduction,
        MediaSegmentType.Recap => AnalysisMode.Recap,
        MediaSegmentType.Preview => AnalysisMode.Preview,
        MediaSegmentType.Outro => AnalysisMode.Credits,
        MediaSegmentType.Commercial => AnalysisMode.Commercial,
        _ => throw new NotImplementedException($"No AnalysisMode mapping for {type}")
    };

    /// <summary>
    /// Parses a segment type string (case-insensitive) into the corresponding <see cref="AnalysisMode"/>.
    /// Handles aliases: both <c>"outro"</c> and <c>"credits"</c> map to <see cref="AnalysisMode.Credits"/>.
    /// </summary>
    /// <param name="type">The segment type string (e.g. "intro", "outro", "credits", "recap", "preview", "commercial").</param>
    /// <returns>The matching <see cref="AnalysisMode"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the string is not a recognized segment type.</exception>
    public static AnalysisMode ParseSegmentType(string type) => type.ToLowerInvariant() switch
    {
        "intro" => AnalysisMode.Introduction,
        "recap" => AnalysisMode.Recap,
        "preview" => AnalysisMode.Preview,
        "outro" or "credits" => AnalysisMode.Credits,
        "commercial" => AnalysisMode.Commercial,
        _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown segment type '{type}'")
    };
}
