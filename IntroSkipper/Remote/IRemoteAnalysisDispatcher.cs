// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Remote;

/// <summary>
/// Queues selected verified analysis work for an external worker without settling local analysis state.
/// </summary>
public interface IRemoteAnalysisDispatcher
{
    /// <summary>
    /// Persists remote work for eligible write targets. The complete comparison set is
    /// retained in each snapshot; only returned ids are removed from local execution.
    /// </summary>
    /// <param name="writeTargets">Verified items that would otherwise be analyzed locally.</param>
    /// <param name="comparisonReferences">Full season references used to calculate the targets.</param>
    /// <param name="mode">The requested plugin analysis mode.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="action">The season's effective analyzer action.</param>
    /// <returns>The write-target ids durably accepted for remote ownership.</returns>
    Task<IReadOnlySet<Guid>> QueueAsync(
        IReadOnlyList<QueuedEpisode> writeTargets,
        IReadOnlyList<QueuedEpisode> comparisonReferences,
        AnalysisMode mode,
        CancellationToken token,
        AnalyzerAction action = AnalyzerAction.Default);
}
