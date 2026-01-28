// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Services;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Media file analyzer interface.
/// </summary>
/// <remarks>
/// Analyzers are instantiated per-analysis run because they maintain per-run state
/// (e.g., fingerprint caches). The <see cref="ISegmentService"/> is passed as a parameter
/// rather than injected via constructor to support this instantiation pattern while
/// still allowing the service to be properly scoped within the DI container.
/// </remarks>
public interface IMediaFileAnalyzer
{
    /// <summary>
    /// Analyze media files for shared introductions or credits, returning all media files that were **not successfully analyzed**.
    /// </summary>
    /// <param name="analysisQueue">Collection of unanalyzed media files.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="segmentService">Segment service for saving results. Passed as parameter because analyzers are created per-run with fresh state.</param>
    /// <param name="cancellationToken">Cancellation token from scheduled task.</param>
    /// <returns>Collection of media files that were **unsuccessfully analyzed**.</returns>
    Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        ISegmentService segmentService,
        CancellationToken cancellationToken);
}
