// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using IntroSkipper.Subtitles;

namespace IntroSkipper.Evaluation;

/// <summary>
/// A thin, media-free orchestration of the recap precedence tiers from RFC D §2.1
/// (Chapter → Subtitle → hardened sting), used to MEASURE the ensemble rather than to run it in
/// production. It exercises the real detection logic — spike A's <see cref="SubtitleRecapSegmentBuilder"/>
/// / <see cref="RecapPhraseMatcher"/> and spike C's <see cref="RecapDetectionHelper"/> — over the
/// synthetic per-episode inputs in <see cref="RecapEpisodeInputs"/>, so the numbers come from the
/// code actually running.
/// </summary>
/// <remarks>
/// Ordering and short-circuit mirror the existing analyzer chain: tiers run in precedence order and
/// each one is skipped if an earlier tier already resolved the episode (the equivalent of
/// <c>NeedsAnalysis</c> short-circuiting). Every tier is behind an enable flag. Boundary handling is
/// the single concern of <see cref="RecapDetectionHelper"/>: the chapter tier carries explicit author
/// boundaries (no inference); the subtitle and sting tiers infer a candidate and then go through the
/// shared reconciliation (hardened) or the legacy behavior (start forced to 0, latest black frame).
/// </remarks>
internal static class RecapTierPipeline
{
    /// <summary>
    /// Backward tolerance (seconds) for the shared end snap — the cue cluster may overshoot a fade
    /// slightly. Generalizes spike A's 1 s tolerance.
    /// </summary>
    private const double EndBackwardTolerance = 1.0;

    /// <summary>
    /// Forward window (seconds) for the shared end snap to the montage fade-out. Generalizes spike
    /// A's <c>BlackFrameSnapSeconds</c> default.
    /// </summary>
    private const double EndForwardWindow = 6.0;

    /// <summary>
    /// Latest cue start (seconds) that may anchor a subtitle recap. Unlike the sting tier this is NOT
    /// clamped to the introduction start — that is precisely how the subtitle tier reaches an
    /// after-intro recap (spike A's structural advantage).
    /// </summary>
    private const double SubtitleAnchorWindowSeconds = 150.0;

    /// <summary>
    /// Runs the enabled tiers in precedence order and returns the winning tier and interval.
    /// </summary>
    /// <param name="inputs">The per-episode signal inputs.</param>
    /// <param name="config">The detector configuration (which tiers, hardened or legacy).</param>
    /// <param name="matcher">The recap-opening phrase matcher for the subtitle tier.</param>
    /// <returns>The outcome (winning tier + interval, or <see cref="RecapTierOutcome.None"/>).</returns>
    public static RecapTierOutcome Detect(RecapEpisodeInputs inputs, RecapDetectorConfig config, RecapPhraseMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(matcher);

        double? introStart = inputs.IntroDetected ? inputs.IntroStart : null;
        var stingMaxBoundary = RecapDetectionHelper.ComputeMaximumBoundary(
            inputs.Duration,
            config.MaximumRecapDetectionDuration,
            introStart);

        // Tier 1 — Chapter (explicit author metadata; authoritative boundaries, no inference).
        if (config.ChapterEnabled && inputs.HasChapterRecap)
        {
            var chapter = BuildChapter(inputs, config);
            if (chapter is { } interval)
            {
                return new RecapTierOutcome(RecapTier.Chapter, interval);
            }
        }

        // Tier 2 — Subtitle (spike A). Skipped when Tier 1 already resolved the episode.
        if (config.SubtitleEnabled && inputs.SubtitleCues.Count > 0)
        {
            var subtitle = BuildSubtitle(inputs, config, matcher);
            if (subtitle is { } interval)
            {
                return new RecapTierOutcome(RecapTier.Subtitle, interval);
            }
        }

        // Tier 3 — Sting + black frame. Skipped when an earlier tier already resolved the episode.
        if (config.StingEnabled && inputs.StingPresent)
        {
            var sting = config.Hardened
                ? BuildHardenedSting(inputs, config, stingMaxBoundary, introStart.HasValue)
                : BuildLegacySting(inputs, config, stingMaxBoundary);
            if (sting is { } interval)
            {
                return new RecapTierOutcome(RecapTier.Sting, interval);
            }
        }

        return RecapTierOutcome.None;
    }

    /// <summary>
    /// Chapter markers are explicit author metadata, so the boundaries are trusted as-is (the shipped
    /// chapter path does the same) — only validated against the duration bounds.
    /// </summary>
    private static RecapInterval? BuildChapter(RecapEpisodeInputs inputs, RecapDetectorConfig config)
    {
        var start = inputs.ChapterRecapStart;
        var end = Math.Min(inputs.ChapterRecapEnd, inputs.Duration);
        if (end <= start)
        {
            return null;
        }

        var duration = end - start;
        return duration >= config.MinimumRecapDuration && duration <= config.MaximumRecapDuration
            ? new RecapInterval(start, end)
            : null;
    }

    /// <summary>
    /// Subtitle tier. The phrase matcher + dense-cue clustering locate the recap; boundaries are then
    /// either reconciled through the shared <see cref="RecapDetectionHelper"/> step (hardened) or left
    /// to spike A's native snapping (legacy).
    /// </summary>
    private static RecapInterval? BuildSubtitle(RecapEpisodeInputs inputs, RecapDetectorConfig config, RecapPhraseMatcher matcher)
    {
        if (config.Hardened)
        {
            // Ensemble: spike A is a pure localizer (its own start/end snapping disabled) and the
            // SHARED reconciler owns the boundaries, so every tier resolves boundaries identically.
            var raw = SubtitleRecapSegmentBuilder.Build(
                inputs.SubtitleCues,
                matcher,
                new SubtitleRecapOptions
                {
                    MaxWindowSeconds = SubtitleAnchorWindowSeconds,
                    MaxDurationSeconds = config.MaximumRecapDuration,
                    MinDurationSeconds = 1, // do not pre-filter; the shared reconciler enforces the real floor
                    SnapStartToZero = false,
                    BlackFrameSnapSeconds = 0, // disable spike A's native end snap; the reconciler does it
                });
            if (raw is null)
            {
                return null;
            }

            var options = new RecapDetectionHelper.RecapBoundaryOptions(
                AllowColdOpen: config.AllowColdOpen,
                MaxBoundary: inputs.Duration, // the subtitle tier is NOT intro-capped (reaches after-intro recaps)
                MinimumRecapDuration: config.MinimumRecapDuration,
                MaximumRecapDuration: config.MaximumRecapDuration,
                EndBackwardTolerance: EndBackwardTolerance,
                EndForwardWindow: EndForwardWindow);
            var reconciled = RecapDetectionHelper.ReconcileBoundaries(raw.Start, raw.End, inputs.BlackFrameTimes, options);
            return reconciled is { } r ? new RecapInterval(r.Start, r.End) : null;
        }

        // +A only: spike A's native behavior — opt-in start-to-0 (off by default) + native end snap.
        var result = SubtitleRecapSegmentBuilder.Build(
            inputs.SubtitleCues,
            matcher,
            new SubtitleRecapOptions
            {
                MaxWindowSeconds = SubtitleAnchorWindowSeconds,
                MaxDurationSeconds = config.MaximumRecapDuration,
            },
            inputs.BlackFrameTimes);
        return result is null ? null : new RecapInterval(result.Start, result.End);
    }

    /// <summary>
    /// Hardened sting tier (spike C): cold-open-aware start, earliest-valid montage end, and the
    /// false-positive guard against the opening theme, via <see cref="RecapDetectionHelper.BuildChromaprintRecap"/>.
    /// </summary>
    private static RecapInterval? BuildHardenedSting(RecapEpisodeInputs inputs, RecapDetectorConfig config, double maxBoundary, bool introDetected)
    {
        var context = new RecapDetectionHelper.RecapBuildContext(
            MaxBoundary: maxBoundary,
            IntroDetected: introDetected,
            AllowColdOpen: config.AllowColdOpen,
            MinimumRecapDuration: config.MinimumRecapDuration,
            MaximumRecapDuration: config.MaximumRecapDuration,
            MinimumRecapDetectionDuration: config.MinimumRecapDetectionDuration);

        var blackFrames = ToBlackFrames(inputs.BlackFrameTimes);
        var sting = new TimeRange(inputs.StingStart, inputs.StingEnd);
        var segment = RecapDetectionHelper.BuildChromaprintRecap(Guid.Empty, sting, blackFrames, context);
        return segment is { Valid: true } ? new RecapInterval(segment.Start, segment.End) : null;
    }

    /// <summary>
    /// Legacy/shipped sting tier: forces the start to 0 and selects the LATEST black frame within the
    /// (intro-clamped) scan window, with no theme false-positive guard. This faithfully models
    /// <c>BuildRecapFromChromaprintCandidate</c> → <c>BuildRecapFromBlackFrames</c>.
    /// </summary>
    private static RecapInterval? BuildLegacySting(RecapEpisodeInputs inputs, RecapDetectorConfig config, double maxBoundary)
    {
        // The only shipped guard: the candidate must fit inside the scan window.
        if (inputs.StingEnd <= 0 || maxBoundary <= inputs.StingEnd)
        {
            return null;
        }

        double? selected = null;
        foreach (var time in inputs.BlackFrameTimes)
        {
            if (time < config.MinimumRecapDetectionDuration || time > maxBoundary)
            {
                continue;
            }

            if (selected is null || time > selected.Value)
            {
                selected = time;
            }
        }

        return selected is { } end ? new RecapInterval(0, end) : null;
    }

    private static List<BlackFrame> ToBlackFrames(IReadOnlyList<double> times)
    {
        var frames = new List<BlackFrame>(times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            frames.Add(new BlackFrame(90, times[i], i));
        }

        return frames;
    }
}
