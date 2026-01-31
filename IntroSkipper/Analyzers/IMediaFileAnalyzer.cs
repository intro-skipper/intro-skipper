// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Media file analyzer interface.
/// </summary>
/// <remarks>
/// Analyzers are instantiated per-analysis run because they maintain per-run state
/// (e.g., fingerprint caches). Detected segments are added to an output collection
/// instead of being saved directly, allowing for batch processing at the end of analysis.
/// </remarks>
public interface IMediaFileAnalyzer
{
    /// <summary>
    /// Analyze media files for shared introductions or credits, returning all media files that were **not successfully analyzed**.
    /// </summary>
    /// <param name="analysisQueue">Collection of unanalyzed media files.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="analyzedSegments">Output collection to add detected segments to. Segments are batched and saved after all analyzers complete.</param>
    /// <param name="cancellationToken">Cancellation token from scheduled task.</param>
    /// <returns>Collection of media files that were **unsuccessfully analyzed**.</returns>
    Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        ICollection<AnalyzedSegment> analyzedSegments,
        CancellationToken cancellationToken);
}
