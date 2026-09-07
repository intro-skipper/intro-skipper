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
    // A sting starting this close to 0:00 opens the episode, so there is no cold open to keep.
    private const double ColdOpenStartThreshold = 5;

    // The fade between a cold open and the recap is looked for this many seconds before the sting.
    private const double ColdOpenLeadInWindow = 10;

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
            if (segment.Type == AnalysisMode.Introduction)
            {
                maximumBoundary = Math.Min(maximumBoundary, TickConversions.ToSeconds(segment.StartTicks));
            }
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

    /// <summary>
    /// Builds a recap from 0 to the latest black frame within the allowed end window.
    /// </summary>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="blackFrames">Black frames from the recap scan window.</param>
    /// <param name="minimumRecapEnd">Earliest allowed recap end in seconds.</param>
    /// <param name="maximumRecapBoundary">Latest allowed recap end in seconds.</param>
    /// <returns>The recap, or <see langword="null"/> when no black frame closes it.</returns>
    internal static Segment? BuildRecapFromBlackFrames(
        Guid episodeId,
        IReadOnlyList<BlackFrame> blackFrames,
        double minimumRecapEnd,
        double maximumRecapBoundary)
    {
        var end = LatestBlackFrameTime(blackFrames, minimumRecapEnd, maximumRecapBoundary);
        return end is null ? null : new Segment(episodeId, new TimeRange(0, end.Value));
    }

    /// <summary>
    /// Builds a recap around a shared Chromaprint sting. The end is the latest black frame before
    /// the boundary, as for the black-frame fallback. The start is 0 unless
    /// <paramref name="anchorToColdOpen"/> is set and the sting begins after
    /// <see cref="ColdOpenStartThreshold"/>; then it is the latest black frame within
    /// <see cref="ColdOpenLeadInWindow"/> before the sting, or the sting start when there is none.
    /// An anchored recap shorter than <paramref name="minimumRecapDuration"/> is rejected.
    /// The caller still runs the result through <see cref="TimeAdjustmentHelper"/>, so a start
    /// within the configured snap threshold of 0 snaps back to 0 and the intro start offset applies.
    /// </summary>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="sting">Shared sting region for this episode.</param>
    /// <param name="blackFrames">Black frames from the recap scan window.</param>
    /// <param name="minimumRecapDuration">Minimum recap length in seconds.</param>
    /// <param name="maximumRecapBoundary">Latest allowed recap end in seconds.</param>
    /// <param name="anchorToColdOpen">Whether a leading cold open moves the start off 0.</param>
    /// <returns>The recap, or <see langword="null"/> when no black frame closes it or it is too short.</returns>
    internal static Segment? BuildRecapFromSting(
        Guid episodeId,
        Segment sting,
        IReadOnlyList<BlackFrame> blackFrames,
        int minimumRecapDuration,
        double maximumRecapBoundary,
        bool anchorToColdOpen)
    {
        var recap = BuildRecapFromBlackFrames(
            episodeId,
            blackFrames,
            Math.Max(minimumRecapDuration, Math.Ceiling(sting.End)),
            maximumRecapBoundary);
        if (recap is null || !anchorToColdOpen || sting.Start <= ColdOpenStartThreshold)
        {
            return recap;
        }

        recap.Start = LatestBlackFrameTime(blackFrames, sting.Start - ColdOpenLeadInWindow, sting.Start) ?? sting.Start;
        return recap.Duration < minimumRecapDuration ? null : recap;
    }

    // The latest black frame time in the closed interval [minimum, maximum], if any.
    private static double? LatestBlackFrameTime(IReadOnlyList<BlackFrame> blackFrames, double minimum, double maximum)
    {
        double? latest = null;
        foreach (var frame in blackFrames)
        {
            if (frame.Time >= minimum && frame.Time <= maximum && (latest is null || frame.Time > latest))
            {
                latest = frame.Time;
            }
        }

        return latest;
    }
}
