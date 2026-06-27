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

        var times = new double[blackFrames.Count];
        for (var i = 0; i < blackFrames.Count; i++)
        {
            times[i] = blackFrames[i].Time;
        }

        return ResolveStartCore(stingStart, times, allowColdOpen);
    }

    /// <summary>
    /// Reconciles a candidate recap interval produced by ANY detection tier (chapter, subtitle, or
    /// sting) into a final interval using one shared start policy and one shared end-refinement
    /// policy. This is the single boundary-resolution step required by RFC D §2.3: it never blanket-
    /// snaps the start to 0 (it anchors a leading cold open instead) and it refines the end to the
    /// nearest black-frame/fade within a small window. Tier-specific localization — e.g. discovering
    /// the montage end from a short sting via <see cref="SelectMontageEnd"/> — happens before this
    /// step; this method owns only the boundaries, so end-boundary semantics no longer differ by
    /// signal (the inconsistency called out in RFC D finding 4).
    /// </summary>
    /// <param name="candidateStart">The tier's proposed recap start in seconds.</param>
    /// <param name="candidateEnd">The tier's proposed recap end in seconds.</param>
    /// <param name="blackFrameTimes">Black-frame timestamps (seconds) in the scan window.</param>
    /// <param name="options">Shared reconciliation bounds and policy.</param>
    /// <returns>The reconciled interval as (start, end), or <see langword="null"/> when no valid recap remains.</returns>
    internal static (double Start, double End)? ReconcileBoundaries(
        double candidateStart,
        double candidateEnd,
        IReadOnlyList<double> blackFrameTimes,
        RecapBoundaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(blackFrameTimes);

        var start = ResolveStartCore(candidateStart, blackFrameTimes, options.AllowColdOpen);
        var end = RefineEndToBlackFrame(candidateEnd, blackFrameTimes, options.EndBackwardTolerance, options.EndForwardWindow);

        // Never extend past the scan ceiling (intro start / configured max / duration).
        if (options.MaxBoundary > 0 && end > options.MaxBoundary)
        {
            end = options.MaxBoundary;
        }

        if (end <= start)
        {
            return null;
        }

        var duration = end - start;
        if (duration > options.MaximumRecapDuration)
        {
            // Clamp an over-long interval to the configured maximum rather than dropping it.
            end = start + options.MaximumRecapDuration;
            duration = options.MaximumRecapDuration;
        }

        if (duration < options.MinimumRecapDuration)
        {
            return null;
        }

        return (start, end);
    }

    /// <summary>
    /// Refines a candidate end boundary to the nearest black-frame/fade within a small window (a
    /// short backward tolerance plus a forward snap window). Shared by every inferring tier so end
    /// semantics no longer differ by signal (RFC D §2.3 #2). Returns the candidate unchanged when no
    /// frame is close enough.
    /// </summary>
    /// <param name="candidateEnd">The proposed end in seconds.</param>
    /// <param name="blackFrameTimes">Black-frame timestamps (seconds) in the scan window.</param>
    /// <param name="backwardTolerance">How far before the candidate a fade may be snapped to (seconds).</param>
    /// <param name="forwardWindow">How far after the candidate a fade may be snapped to (seconds).</param>
    /// <returns>The refined end in seconds.</returns>
    internal static double RefineEndToBlackFrame(
        double candidateEnd,
        IReadOnlyList<double> blackFrameTimes,
        double backwardTolerance,
        double forwardWindow)
    {
        ArgumentNullException.ThrowIfNull(blackFrameTimes);

        var best = candidateEnd;
        var bestDelta = double.MaxValue;
        foreach (var time in blackFrameTimes)
        {
            var delta = time - candidateEnd;
            if (delta >= -backwardTolerance && delta <= forwardWindow && Math.Abs(delta) < bestDelta)
            {
                bestDelta = Math.Abs(delta);
                best = time;
            }
        }

        return best;
    }

    /// <summary>
    /// The single cold-open-aware start policy shared by the Chromaprint sting path
    /// (<see cref="ResolveRecapStart"/>) and the tier-agnostic <see cref="ReconcileBoundaries"/>:
    /// snap to 0 only when the candidate already opens the episode, otherwise anchor to the fade
    /// just before it, and never blanket-force 0 (RFC D §2.3 #3).
    /// </summary>
    /// <param name="candidateStart">Candidate start in seconds.</param>
    /// <param name="blackFrameTimes">Black-frame timestamps (seconds) in the scan window.</param>
    /// <param name="allowColdOpen">Whether non-zero (cold-open) starts are permitted.</param>
    /// <returns>The resolved start in seconds.</returns>
    private static double ResolveStartCore(double candidateStart, IReadOnlyList<double> blackFrameTimes, bool allowColdOpen)
    {
        // Legacy behavior: always begin the recap at the episode start.
        if (!allowColdOpen)
        {
            return 0;
        }

        // A candidate that begins at (or within a few seconds of) 0:00 indicates the recap opens the
        // episode; there is no cold open to preserve.
        if (candidateStart <= ColdOpenStartThreshold)
        {
            return 0;
        }

        // A cold open precedes the recap. Anchor the start to the fade/black-frame transition just
        // before the candidate (closest qualifying frame) when one exists, otherwise to the candidate.
        double? transition = null;
        foreach (var time in blackFrameTimes)
        {
            if (time > candidateStart || time < candidateStart - ColdOpenLeadInWindow)
            {
                continue;
            }

            if (transition is null || time > transition.Value)
            {
                transition = time;
            }
        }

        return transition ?? candidateStart;
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

    /// <summary>
    /// Shared boundary-reconciliation policy consumed by <see cref="ReconcileBoundaries"/>. Carries
    /// the cold-open toggle, the scan ceiling, the duration bounds, and the end-snap window so every
    /// tier reconciles boundaries identically.
    /// </summary>
    /// <param name="AllowColdOpen">Whether non-zero (cold-open) recap starts are permitted.</param>
    /// <param name="MaxBoundary">Latest timestamp (seconds) the recap end may reach; 0 disables the clamp.</param>
    /// <param name="MinimumRecapDuration">Minimum acceptable recap duration in seconds.</param>
    /// <param name="MaximumRecapDuration">Maximum acceptable recap duration in seconds.</param>
    /// <param name="EndBackwardTolerance">How far before the candidate end a fade may be snapped to (seconds).</param>
    /// <param name="EndForwardWindow">How far after the candidate end a fade may be snapped to (seconds).</param>
    internal readonly record struct RecapBoundaryOptions(
        bool AllowColdOpen,
        double MaxBoundary,
        double MinimumRecapDuration,
        double MaximumRecapDuration,
        double EndBackwardTolerance,
        double EndForwardWindow);
}
