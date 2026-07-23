// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Immutable season-scoped snapshot used during queue verification to avoid per-episode database lookups.
/// </summary>
/// <param name="EpisodeIdsByMode">Episode identifiers grouped by analysis mode.</param>
/// <param name="ConfigHashByMode">Configuration hashes grouped by analysis mode.</param>
/// <param name="AnalyzerActionByMode">Analyzer actions grouped by analysis mode.</param>
/// <param name="DisabledEpisodeIds">Episodes explicitly disabled for analysis.</param>
/// <param name="SegmentsByEpisodeId">Existing segments grouped by episode and analysis mode.</param>
/// <param name="UserProvidedByMode">User-provided episode identifiers grouped by analysis mode.</param>
internal sealed record SeasonQueueSnapshot(
    IReadOnlyDictionary<AnalysisMode, IReadOnlySet<Guid>> EpisodeIdsByMode,
    IReadOnlyDictionary<AnalysisMode, string> ConfigHashByMode,
    IReadOnlyDictionary<AnalysisMode, AnalyzerAction> AnalyzerActionByMode,
    IReadOnlySet<Guid> DisabledEpisodeIds,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<AnalysisMode, Segment>> SegmentsByEpisodeId,
    IReadOnlyDictionary<AnalysisMode, IReadOnlySet<Guid>> UserProvidedByMode);
