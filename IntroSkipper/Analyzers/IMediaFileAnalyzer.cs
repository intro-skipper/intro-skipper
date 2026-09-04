// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Media file analyzer interface.
/// </summary>
internal interface IMediaFileAnalyzer
{
    /// <summary>
    /// Analyzes the media files that still need analysis for the mode (see
    /// <see cref="QueuedEpisode.NeedsAnalysis"/>) and marks the ones it found segments
    /// for as analyzed, so the next analyzer in the chain skips them.
    /// </summary>
    /// <param name="analysisQueue">The season's queued media files, analyzed or not.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token from scheduled task.</param>
    /// <returns>The same collection, with the per-mode analysis state of each file updated in place.</returns>
    Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken);
}
