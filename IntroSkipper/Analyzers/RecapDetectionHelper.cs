// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Shared recap detection helpers.
/// </summary>
internal static class RecapDetectionHelper
{
    /// <summary>
    /// Gets the latest timestamp that recap boundary detection should scan.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The maximum recap boundary in seconds.</returns>
    internal static async Task<double> GetMaximumBoundaryAsync(
        QueuedEpisode episode,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var maximumBoundary = Math.Min(episode.Duration, config.MaximumRecapDetectionDuration);
        var timestamps = await Plugin.Instance!.GetTimestampsAsync(
            episode.EpisodeId,
            cancellationToken).ConfigureAwait(false);
        if (timestamps.TryGetValue(AnalysisMode.Introduction, out var intro) && intro.Valid)
        {
            maximumBoundary = Math.Min(maximumBoundary, intro.Start);
        }

        return maximumBoundary;
    }
}
