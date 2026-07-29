// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;

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
        var timestamps = await database.GetTimestampsAsync(
            episode.EpisodeId,
            cancellationToken).ConfigureAwait(false);
        if (timestamps.TryGetValue(AnalysisMode.Introduction, out var intro) && intro.Valid)
        {
            maximumBoundary = Math.Min(maximumBoundary, intro.Start);
        }

        return maximumBoundary;
    }

    /// <summary>
    /// Scans for recap black frames and applies adaptive threshold normalization to the
    /// full darkness distribution, so every recap consumer shares one definition of "black".
    /// </summary>
    /// <param name="ffmpegService">FFmpeg service.</param>
    /// <param name="episode">Queued episode.</param>
    /// <param name="maxRecapBoundary">The latest timestamp the scan should cover.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The black frames that satisfy the normalized threshold.</returns>
    internal static async Task<BlackFrame[]> DetectAdaptiveBlackFramesAsync(
        IFFmpegService ffmpegService,
        QueuedEpisode episode,
        double maxRecapBoundary,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        // Request the scan unfiltered (minimum 0): NormalizeThreshold measures the content's
        // baseline darkness from the full distribution before the configured percentage applies.
        var blackFrames = await ffmpegService.DetectBlackFramesAsync(
            episode,
            new TimeRange(0, maxRecapBoundary),
            0,
            config.BlackFrameThreshold,
            AnalysisMode.Recap,
            cancellationToken).ConfigureAwait(false);
        if (blackFrames.Length == 0)
        {
            return [];
        }

        var (minimum, _) = BlackFrameThresholdHelper.NormalizeThreshold(blackFrames, config.BlackFrameMinimumPercentage);
        return [.. blackFrames.Where(frame => frame.Percentage >= minimum)];
    }
}
