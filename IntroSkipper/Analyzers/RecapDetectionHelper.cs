// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Shared recap detection helpers.
/// </summary>
/// <remarks>
/// This type is the single source of truth for the recap scan window and for turning a
/// Chromaprint "previously on" sting plus black-frame structure into a recap segment. Keeping the
/// clamp and boundary logic here (rather than duplicated inline in the analyzers) means both the
/// Chromaprint path and the black-frame fallback compute identical windows.
/// </remarks>
internal static class RecapDetectionHelper
{
    /// <summary>
    /// A sting that begins at or before this offset (seconds) is treated as opening the episode,
    /// so the recap starts at 0:00. A later sting implies a leading cold open whose boundary must
    /// be preserved.
    /// </summary>
    private const double ColdOpenStartThreshold = 5.0;

    /// <summary>
    /// How far (seconds) before the sting to look for the fade/black-frame transition that marks
    /// the cold-open → recap boundary. A frame further back than this is more likely an internal
    /// cold-open scene cut than the recap boundary, so the sting start is used instead.
    /// </summary>
    private const double ColdOpenLeadInWindow = 6.0;

    /// <summary>
    /// When no introduction has been detected, a shared region longer than this (seconds) is
    /// treated as the opening theme rather than a recap sting and rejected. Without the
    /// intro-clamped scan window this is the only signal separating a recurring theme from a short
    /// "previously on" sting; it is deliberately conservative and is a documented source of missed
    /// long-sting recaps.
    /// </summary>
    private const double StingMaximumDuration = 20.0;

    /// <summary>
    /// Computes the latest timestamp (seconds) recap boundary detection should scan, clamped to the
    /// configured detection ceiling, the media duration, and — when supplied — the introduction
    /// start. This is the single, pure implementation of the recap window clamp.
    /// </summary>
    /// <param name="duration">Episode duration in seconds.</param>
    /// <param name="maximumDetectionDuration">Configured maximum recap detection duration in seconds.</param>
    /// <param name="introStart">Introduction start in seconds, or <see langword="null"/> when no valid intro exists.</param>
    /// <returns>The maximum recap boundary in seconds.</returns>
    internal static double ComputeMaximumBoundary(double duration, int maximumDetectionDuration, double? introStart)
    {
        var boundary = Math.Min(duration, maximumDetectionDuration);
        if (introStart is { } introStartValue)
        {
            boundary = Math.Min(boundary, introStartValue);
        }

        return boundary;
    }

    /// <summary>
    /// Gets the recap scan window for an episode: the maximum boundary plus whether a valid
    /// introduction was detected (which controls how strict the false-positive guard must be).
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recap scan window.</returns>
    internal static async Task<RecapScanWindow> GetRecapScanWindowAsync(
        QueuedEpisode episode,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(config);

        double? introStart = null;
        var timestamps = await Plugin.GetTimestampsAsync(
            episode.EpisodeId,
            cancellationToken).ConfigureAwait(false);
        if (timestamps.TryGetValue(AnalysisMode.Introduction, out var intro) && intro.Valid)
        {
            introStart = intro.Start;
        }

        var maximumBoundary = ComputeMaximumBoundary(episode.Duration, config.MaximumRecapDetectionDuration, introStart);
        return new RecapScanWindow(maximumBoundary, introStart.HasValue);
    }

    /// <summary>
    /// Builds a recap segment from a Chromaprint shared "previously on" sting and the black-frame
    /// structure around it. The start is anchored to a leading cold open (if present) rather than
    /// forced to 0:00, the end is the earliest fade/black-frame transition that closes the montage,
    /// and the result is duration-sanity checked and guarded against the opening theme.
    /// </summary>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="sting">The shared (sting) region returned by the index search.</param>
    /// <param name="blackFrames">Black frames detected in the scan window.</param>
    /// <param name="context">Recap window and configuration bounds.</param>
    /// <returns>The recap segment, or <see langword="null"/> when no confident recap is found.</returns>
    internal static Segment? BuildChromaprintRecap(
        Guid episodeId,
        TimeRange sting,
        IReadOnlyList<BlackFrame> blackFrames,
        RecapBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(sting);
        ArgumentNullException.ThrowIfNull(blackFrames);

        // The shared region must be a real candidate that fits inside the scan window. When an
        // intro is detected this also rejects a sting that coincides with the intro theme, because
        // the window has already been clamped to the intro start.
        if (sting.End <= 0 || context.MaxBoundary <= sting.End)
        {
            return null;
        }

        // False-positive guard: with no intro detected the window is not clamped to the intro, so a
        // long early shared region is far more likely to be the recurring theme than a recap sting.
        if (!context.IntroDetected && sting.Duration > StingMaximumDuration)
        {
            return null;
        }

        var start = ResolveRecapStart(sting.Start, blackFrames, context.AllowColdOpen);
        var minimumEndTime = Math.Max(context.MinimumRecapDetectionDuration, sting.End);
        var end = SelectMontageEnd(blackFrames, start, sting.End, minimumEndTime, context);

        if (end is null)
        {
            // No montage-end black frame exists. Only trust a no-black-frame recap when an intro
            // was detected (theme already excluded) and the shared region is itself long enough to
            // be the recap body — i.e. a shared music bed spanning the whole montage. Otherwise we
            // cannot bound the recap and refuse to guess (which would risk swallowing the episode).
            if (context.IntroDetected && sting.Duration >= context.MinimumRecapDuration)
            {
                end = sting.End;
            }
            else
            {
                return null;
            }
        }

        var recap = new TimeRange(start, end.Value);
        return IsWithinBounds(recap, context) ? new Segment(episodeId, recap) : null;
    }

    /// <summary>
    /// Resolves the recap start, anchoring it to a leading cold open when one is present instead of
    /// forcing 0:00.
    /// </summary>
    /// <param name="stingStart">Start of the shared sting in seconds.</param>
    /// <param name="blackFrames">Black frames detected in the scan window.</param>
    /// <param name="allowColdOpen">Whether non-zero (cold-open) starts are permitted.</param>
    /// <returns>The resolved recap start in seconds.</returns>
    internal static double ResolveRecapStart(double stingStart, IReadOnlyList<BlackFrame> blackFrames, bool allowColdOpen)
    {
        ArgumentNullException.ThrowIfNull(blackFrames);

        // Legacy behavior: always begin the recap at the episode start.
        if (!allowColdOpen)
        {
            return 0;
        }

        // A sting that begins at (or within a few seconds of) 0:00 indicates the recap opens the
        // episode; there is no cold open to preserve.
        if (stingStart <= ColdOpenStartThreshold)
        {
            return 0;
        }

        // A cold open precedes the recap. Anchor the start to the fade/black-frame transition just
        // before the sting (closest qualifying frame) when one exists, otherwise to the sting.
        double? transition = null;
        foreach (var blackFrame in blackFrames)
        {
            if (blackFrame.Time > stingStart || blackFrame.Time < stingStart - ColdOpenLeadInWindow)
            {
                continue;
            }

            if (transition is null || blackFrame.Time > transition.Value)
            {
                transition = blackFrame.Time;
            }
        }

        return transition ?? stingStart;
    }

    /// <summary>
    /// Selects the montage-end black frame: the earliest fade/black-frame transition after the
    /// sting that produces a recap whose duration is within the configured bounds. Choosing the
    /// earliest valid frame (rather than the latest) prevents the boundary from overshooting into a
    /// mid-episode scene change, while the duration floor skips transitions too close to the start.
    /// </summary>
    /// <param name="blackFrames">Black frames detected in the scan window.</param>
    /// <param name="start">Resolved recap start in seconds.</param>
    /// <param name="stingEnd">End of the shared sting in seconds.</param>
    /// <param name="minimumEndTime">Earliest allowed recap end time in seconds.</param>
    /// <param name="context">Recap window and configuration bounds.</param>
    /// <returns>The montage-end time in seconds, or <see langword="null"/> when no frame qualifies.</returns>
    internal static double? SelectMontageEnd(
        IReadOnlyList<BlackFrame> blackFrames,
        double start,
        double stingEnd,
        double minimumEndTime,
        RecapBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(blackFrames);

        double? best = null;
        foreach (var blackFrame in blackFrames)
        {
            var time = blackFrame.Time;
            if (time <= stingEnd || time < minimumEndTime || time > context.MaxBoundary)
            {
                continue;
            }

            var duration = time - start;
            if (duration < context.MinimumRecapDuration || duration > context.MaximumRecapDuration)
            {
                continue;
            }

            if (best is null || time < best.Value)
            {
                best = time;
            }
        }

        return best;
    }

    private static bool IsWithinBounds(TimeRange recap, RecapBuildContext context)
    {
        if (recap.Start < 0 || recap.End <= recap.Start || recap.End > context.MaxBoundary)
        {
            return false;
        }

        var duration = recap.Duration;
        return duration >= context.MinimumRecapDuration && duration <= context.MaximumRecapDuration;
    }

    /// <summary>
    /// The recap scan window for an episode.
    /// </summary>
    /// <param name="MaxBoundary">Latest timestamp (seconds) recap detection should scan.</param>
    /// <param name="IntroDetected">Whether a valid introduction segment exists for the episode.</param>
    internal readonly record struct RecapScanWindow(double MaxBoundary, bool IntroDetected);

    /// <summary>
    /// Configuration bounds and scan state passed to <see cref="BuildChromaprintRecap"/>.
    /// </summary>
    /// <param name="MaxBoundary">Latest timestamp (seconds) recap detection should scan.</param>
    /// <param name="IntroDetected">Whether a valid introduction segment exists for the episode.</param>
    /// <param name="AllowColdOpen">Whether non-zero (cold-open) recap starts are permitted.</param>
    /// <param name="MinimumRecapDuration">Minimum acceptable recap duration in seconds.</param>
    /// <param name="MaximumRecapDuration">Maximum acceptable recap duration in seconds.</param>
    /// <param name="MinimumRecapDetectionDuration">Earliest allowed recap end time in seconds.</param>
    internal readonly record struct RecapBuildContext(
        double MaxBoundary,
        bool IntroDetected,
        bool AllowColdOpen,
        int MinimumRecapDuration,
        int MaximumRecapDuration,
        int MinimumRecapDetectionDuration);
}
