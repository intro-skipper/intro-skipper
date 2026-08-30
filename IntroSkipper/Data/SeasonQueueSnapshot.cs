// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Immutable season-scoped snapshot used during queue verification to avoid per-episode database lookups.
/// </summary>
/// <param name="AnalyzedConfigHashes">Configuration hash each episode was last analyzed under, keyed by episode and analysis mode; absent when the episode was never analyzed for the mode.</param>
/// <param name="AnalyzerActionByMode">Analyzer actions grouped by analysis mode.</param>
/// <param name="SegmentModesByEpisodeId">Analysis modes with at least one active segment, keyed by episode. Presence only: queue verification never reads segment boundaries, so the snapshot carries no timing payload.</param>
/// <param name="UserProvidedByMode">Episode identifiers with at least one active user-provided segment, grouped by analysis mode.</param>
public sealed record SeasonQueueSnapshot(
    IReadOnlyDictionary<(Guid ItemId, AnalysisMode Mode), string> AnalyzedConfigHashes,
    IReadOnlyDictionary<AnalysisMode, AnalyzerAction> AnalyzerActionByMode,
    IReadOnlyDictionary<Guid, IReadOnlySet<AnalysisMode>> SegmentModesByEpisodeId,
    IReadOnlyDictionary<AnalysisMode, IReadOnlySet<Guid>> UserProvidedByMode);
