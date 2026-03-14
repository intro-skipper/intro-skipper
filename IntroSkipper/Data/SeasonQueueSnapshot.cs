// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Immutable season-scoped snapshot used during queue verification to avoid per-episode database lookups.
/// </summary>
/// <param name="EpisodeIdsByMode">Episode identifiers grouped by analysis mode.</param>
/// <param name="SegmentsByEpisodeId">Existing segments grouped by episode and analysis mode.</param>
internal sealed record SeasonQueueSnapshot(
    IReadOnlyDictionary<AnalysisMode, IReadOnlySet<Guid>> EpisodeIdsByMode,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<AnalysisMode, Segment>> SegmentsByEpisodeId);
