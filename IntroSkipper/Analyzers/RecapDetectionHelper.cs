// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Shared recap detection helpers.
/// </summary>
internal static class RecapDetectionHelper
{
    /// <summary>
    /// Gets the latest timestamp that recap boundary detection should scan.
    /// </summary>
    /// <param name="database">Segment database facade.</param>
    /// <param name="episode">Queued episode.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The maximum recap boundary in seconds.</returns>
    internal static async Task<double> GetMaximumBoundaryAsync(
        IIntroSkipperDatabase database,
        QueuedEpisode episode,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var maximumBoundary = Math.Min(episode.Duration, config.MaximumRecapDetectionDuration);
        var segments = await database.GetSegmentsAsync(
            episode.EpisodeId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // A recap must end before the earliest stored introduction begins.
        foreach (var segment in segments)
        {
            if (segment.Type == AnalysisMode.Introduction && segment.EndTicks > 0)
            {
                maximumBoundary = Math.Min(maximumBoundary, TickConversions.ToSeconds(segment.StartTicks));
            }
        }

        return maximumBoundary;
    }
}
