// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// A named detector configuration: which tiers are enabled and whether the hardened (spike C)
/// boundary logic is used. These map directly onto the four comparison columns required by the
/// round-2 measurement (baseline / +C / +A / +A+C). Duration bounds mirror the plugin defaults so
/// the pipeline reconciles boundaries with the same floors/ceilings production uses.
/// </summary>
/// <param name="Name">Human-readable column name for the comparison report.</param>
/// <param name="ChapterEnabled">Whether the chapter tier (Tier 1) runs.</param>
/// <param name="SubtitleEnabled">Whether the subtitle tier (Tier 2, spike A) runs.</param>
/// <param name="StingEnabled">Whether the sting tier (Tier 3) runs.</param>
/// <param name="Hardened">
/// When true the sting and subtitle tiers use the hardened, cold-open-aware boundary logic and
/// false-positive guard (spike C + the shared reconciler). When false they use the legacy behavior:
/// the sting path forces the start to 0 and selects the latest black frame, with no theme guard.
/// </param>
internal sealed record RecapDetectorConfig(
    string Name,
    bool ChapterEnabled,
    bool SubtitleEnabled,
    bool StingEnabled,
    bool Hardened)
{
    /// <summary>
    /// Gets the minimum acceptable recap duration in seconds (plugin default).
    /// </summary>
    public int MinimumRecapDuration { get; init; } = 15;

    /// <summary>
    /// Gets the maximum acceptable recap duration in seconds (plugin default).
    /// </summary>
    public int MaximumRecapDuration { get; init; } = 120;

    /// <summary>
    /// Gets the minimum recap end time in seconds for the legacy/sting path (plugin default).
    /// </summary>
    public int MinimumRecapDetectionDuration { get; init; } = 15;

    /// <summary>
    /// Gets the maximum recap detection (scan-window) duration in seconds (plugin default).
    /// </summary>
    public int MaximumRecapDetectionDuration { get; init; } = 120;

    /// <summary>
    /// Gets a value indicating whether non-zero (cold-open) starts are permitted in the hardened path.
    /// </summary>
    public bool AllowColdOpen { get; init; } = true;

    /// <summary>
    /// Gets the shipped baseline: chapter + legacy sting (start forced to 0, latest black frame, no guard).
    /// </summary>
    public static RecapDetectorConfig Baseline { get; } =
        new("baseline (shipped)", ChapterEnabled: true, SubtitleEnabled: false, StingEnabled: true, Hardened: false);

    /// <summary>
    /// Gets the hardening-only config: chapter + hardened sting (spike C), no subtitle tier.
    /// </summary>
    public static RecapDetectorConfig HardeningOnly { get; } =
        new("+C hardening", ChapterEnabled: true, SubtitleEnabled: false, StingEnabled: true, Hardened: true);

    /// <summary>
    /// Gets the subtitles-only config: chapter + subtitle (spike A, native boundaries) + legacy sting.
    /// </summary>
    public static RecapDetectorConfig SubtitlesOnly { get; } =
        new("+A subtitles", ChapterEnabled: true, SubtitleEnabled: true, StingEnabled: true, Hardened: false);

    /// <summary>
    /// Gets the full tiered ensemble: chapter → subtitle (spike A) → hardened sting (spike C), all
    /// using the shared boundary reconciler.
    /// </summary>
    public static RecapDetectorConfig Ensemble { get; } =
        new("+A+C ensemble", ChapterEnabled: true, SubtitleEnabled: true, StingEnabled: true, Hardened: true);

    /// <summary>
    /// Gets the four standard comparison configurations, in report order.
    /// </summary>
    public static IReadOnlyList<RecapDetectorConfig> Standard { get; } =
        [Baseline, HardeningOnly, SubtitlesOnly, Ensemble];
}
