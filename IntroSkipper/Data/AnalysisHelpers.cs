// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Data;

/// <summary>
/// Pure analysis helper functions with no database or plugin state access.
/// </summary>
internal static class AnalysisHelpers
{
    /// <summary>
    /// Gets the single source of truth for the correspondence between analysis modes and
    /// Jellyfin media segment types; <see cref="TryMapSegmentTypeToMode"/> is derived from it.
    /// </summary>
    internal static IReadOnlyDictionary<AnalysisMode, MediaSegmentType> ModeToSegmentType { get; } = new Dictionary<AnalysisMode, MediaSegmentType>
    {
        [AnalysisMode.Introduction] = MediaSegmentType.Intro,
        [AnalysisMode.Recap] = MediaSegmentType.Recap,
        [AnalysisMode.Preview] = MediaSegmentType.Preview,
        [AnalysisMode.Credits] = MediaSegmentType.Outro,
        [AnalysisMode.Commercial] = MediaSegmentType.Commercial
    };

    // Must be declared after ModeToSegmentType: property initializers run in textual order.
    private static IReadOnlyDictionary<MediaSegmentType, AnalysisMode> SegmentTypeToMode { get; } =
        ModeToSegmentType.ToDictionary(pair => pair.Value, pair => pair.Key);

    private static IReadOnlyDictionary<string, AnalysisMode> SegmentTypeNameToMode { get; } =
        ModeToSegmentType.ToDictionary(pair => pair.Value.ToString(), pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether the mode has a <see cref="ModeToSegmentType"/> entry, i.e. every
    /// downstream conversion of a row carrying it is defined. Write boundaries and HTTP
    /// edges reject modes failing this so no stored row can crash a later mirror; a mere
    /// <c>Enum.IsDefined</c> check would drift the moment a mode is added without a
    /// mapping.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns><see langword="true"/> when the mode is mappable; otherwise <see langword="false"/>.</returns>
    internal static bool IsSupported(AnalysisMode mode) => ModeToSegmentType.ContainsKey(mode);

    /// <summary>
    /// Maps a Jellyfin media segment type to its analysis mode; derived from
    /// <see cref="ModeToSegmentType"/>. <see cref="MediaSegmentType.Unknown"/> is a
    /// defined enum value with no mapping (and the default for an omitted JSON
    /// property), so callers must handle <see langword="null"/> — an
    /// <c>Enum.IsDefined</c> pre-check would not catch it.
    /// </summary>
    /// <param name="type">Media segment type.</param>
    /// <returns>The corresponding mode, or <see langword="null"/> for unmapped types.</returns>
    internal static AnalysisMode? TryMapSegmentTypeToMode(MediaSegmentType type)
        => SegmentTypeToMode.TryGetValue(type, out var mode) ? mode : null;

    /// <summary>
    /// Parses a <see cref="MediaSegmentType"/> enum name (case-insensitive) to its analysis
    /// mode; derived from <see cref="ModeToSegmentType"/>.
    /// </summary>
    /// <param name="name">Segment type name, e.g. <c>"Intro"</c> or <c>"outro"</c>.</param>
    /// <returns>The corresponding mode, or <see langword="null"/> for unknown names.</returns>
    internal static AnalysisMode? TryParseSegmentTypeName(string name)
        => SegmentTypeNameToMode.TryGetValue(name, out var mode) ? mode : null;
}
