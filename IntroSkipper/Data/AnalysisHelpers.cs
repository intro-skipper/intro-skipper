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
    /// Jellyfin media segment types; <see cref="MapSegmentTypeToMode"/> is derived from it.
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

    /// <summary>
    /// Gets the analysis modes managed by the segment editor.
    /// </summary>
    /// <remarks>
    /// The collection contains one mode per mapped Jellyfin segment type and must be
    /// declared after <see cref="ModeToSegmentType"/> because property initialization is textual.
    /// </remarks>
    /// <value>The analysis modes with a mapped Jellyfin segment type.</value>
    internal static IReadOnlyList<AnalysisMode> EditorManagedModes { get; } = [.. ModeToSegmentType.Keys];

    /// <summary>
    /// Returns whether a settled-season analysis mode still needs re-analysis for its current episode
    /// set. Pure set comparison: the decision is committed separately via
    /// <see cref="Db.IIntroSkipperDatabase.RecordSettleReanalysisAsync(Guid, IReadOnlyCollection{AnalysisMode}, IReadOnlyCollection{Guid}, CancellationToken)"/>
    /// once the reset has succeeded, so the completed episode set survives plugin restarts.
    /// </summary>
    /// <param name="settledEpisodeIds">Episode IDs recorded when the season was last settle-reanalyzed for this mode.</param>
    /// <param name="episodeIds">Current episode IDs in the season.</param>
    /// <returns><see langword="true"/> when a re-analysis should be performed; otherwise <see langword="false"/>.</returns>
    internal static bool ShouldSettleReanalyze(
        IReadOnlySet<Guid> settledEpisodeIds,
        IReadOnlyCollection<Guid> episodeIds)
        => settledEpisodeIds.Count != episodeIds.Count || episodeIds.Any(id => !settledEpisodeIds.Contains(id));

    /// <summary>
    /// Maps a Jellyfin media segment type to the corresponding analysis mode.
    /// </summary>
    /// <param name="type">Media segment type.</param>
    /// <returns>The corresponding <see cref="AnalysisMode"/>.</returns>
    internal static AnalysisMode MapSegmentTypeToMode(MediaSegmentType type)
        => SegmentTypeToMode.TryGetValue(type, out var mode) ? mode : throw new NotImplementedException();

    /// <summary>
    /// Maps a Jellyfin media segment type to the corresponding analysis mode without
    /// throwing on unmapped types, so request handlers can reject unknown input as a
    /// client error instead of surfacing a server error.
    /// </summary>
    /// <param name="type">Media segment type.</param>
    /// <param name="mode">The corresponding <see cref="AnalysisMode"/> when the type maps.</param>
    /// <returns><see langword="true"/> when the type maps to a mode; otherwise <see langword="false"/>.</returns>
    internal static bool TryMapSegmentTypeToMode(MediaSegmentType type, out AnalysisMode mode)
        => SegmentTypeToMode.TryGetValue(type, out mode);
}
