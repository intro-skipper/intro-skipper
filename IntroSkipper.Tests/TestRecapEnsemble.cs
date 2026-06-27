// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using IntroSkipper.Analyzers;
using IntroSkipper.Evaluation;
using IntroSkipper.Subtitles;
using Xunit;

/// <summary>
/// Tests for the round-2 recap ensemble: the shared boundary reconciliation, the tiered pipeline
/// (ordering + per-tier enable flags + skip-when-resolved), the harm-aware metrics (content-skip
/// seconds, silent-miss vs fired-but-wrong split), and the end-to-end config comparison whose
/// relationships are the acceptance check. The comparison is also (optionally) written to disk so
/// the measurement doc's table can be regenerated from the harness actually running.
/// </summary>
public class TestRecapEnsemble
{
    private const double Tolerance = 1e-6;

    // ---- Shared boundary reconciliation (RFC D §2.3) ----

    [Fact]
    public void ReconcileBoundaries_ColdOpenCandidate_AnchorsStartToFadeNotZero()
    {
        // Cold-open recap candidate [52,87]; fades at the cold-open boundary (52) and montage end (88).
        var options = new RecapDetectionHelper.RecapBoundaryOptions(
            AllowColdOpen: true, MaxBoundary: 2600, MinimumRecapDuration: 15, MaximumRecapDuration: 120,
            EndBackwardTolerance: 1.0, EndForwardWindow: 6.0);

        var result = RecapDetectionHelper.ReconcileBoundaries(52, 87, [52, 88], options);

        Assert.NotNull(result);
        Assert.Equal(52, result!.Value.Start, Tolerance); // NOT 0 — cold open preserved
        Assert.Equal(88, result.Value.End, Tolerance);    // snapped to the montage fade
    }

    [Fact]
    public void ReconcileBoundaries_StartNearZero_SnapsToZero()
    {
        var options = new RecapDetectionHelper.RecapBoundaryOptions(
            AllowColdOpen: true, MaxBoundary: 2700, MinimumRecapDuration: 15, MaximumRecapDuration: 120,
            EndBackwardTolerance: 1.0, EndForwardWindow: 6.0);

        var result = RecapDetectionHelper.ReconcileBoundaries(1, 27, [30], options);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Start, Tolerance);   // recap opens the episode
        Assert.Equal(30, result.Value.End, Tolerance);     // end snapped forward to the fade
    }

    [Fact]
    public void ReconcileBoundaries_BelowMinimumDuration_Rejected()
    {
        var options = new RecapDetectionHelper.RecapBoundaryOptions(
            AllowColdOpen: true, MaxBoundary: 600, MinimumRecapDuration: 15, MaximumRecapDuration: 120,
            EndBackwardTolerance: 1.0, EndForwardWindow: 6.0);

        // Candidate [50,58] with no nearby fade => 8 s, below the 15 s floor.
        var result = RecapDetectionHelper.ReconcileBoundaries(50, 58, [], options);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(27, new[] { 30.0 }, 30)]   // snap forward to a fade 3 s away
    [InlineData(27, new[] { 50.0 }, 27)]   // fade too far away => unchanged
    [InlineData(88, new[] { 87.5 }, 87.5)] // small backward tolerance
    public void RefineEndToBlackFrame_SnapsWithinWindowOnly(double candidateEnd, double[] frames, double expected)
    {
        var refined = RecapDetectionHelper.RefineEndToBlackFrame(candidateEnd, frames, backwardTolerance: 1.0, forwardWindow: 6.0);
        Assert.Equal(expected, refined, Tolerance);
    }

    // ---- Tiered pipeline ordering + enable flags ----

    [Fact]
    public void Pipeline_ChapterWinsOverSubtitleAndSting()
    {
        var inputs = new RecapEpisodeInputs
        {
            Duration = 2000, IntroDetected = true, IntroStart = 95,
            HasChapterRecap = true, ChapterRecapStart = 0, ChapterRecapEnd = 30,
            StingPresent = true, StingStart = 0.5, StingEnd = 4,
            BlackFrameTimes = { 30 },
            SubtitleCues = { new SubtitleCue(1, 4, "Previously on...") },
        };

        var outcome = RecapTierPipeline.Detect(inputs, RecapDetectorConfig.Ensemble, RecapPhraseMatcher.Default);

        Assert.Equal(RecapTier.Chapter, outcome.Tier);
    }

    [Fact]
    public void Pipeline_SubtitleWinsOverStingWhenNoChapter()
    {
        var inputs = ColdOpenWithSubtitlesAndSting();

        var outcome = RecapTierPipeline.Detect(inputs, RecapDetectorConfig.Ensemble, RecapPhraseMatcher.Default);

        Assert.Equal(RecapTier.Subtitle, outcome.Tier);
    }

    [Fact]
    public void Pipeline_StingUsedWhenSubtitleTierDisabled()
    {
        var inputs = ColdOpenWithSubtitlesAndSting();

        // +C hardening has the subtitle tier disabled, so the sting tier resolves it.
        var outcome = RecapTierPipeline.Detect(inputs, RecapDetectorConfig.HardeningOnly, RecapPhraseMatcher.Default);

        Assert.Equal(RecapTier.Sting, outcome.Tier);
        Assert.Equal(52, outcome.Interval.Start, Tolerance); // hardened start, not 0
    }

    [Fact]
    public void Pipeline_BaselineForcesColdOpenStartToZero()
    {
        var inputs = ColdOpenWithSubtitlesAndSting();

        var outcome = RecapTierPipeline.Detect(inputs, RecapDetectorConfig.Baseline, RecapPhraseMatcher.Default);

        Assert.Equal(RecapTier.Sting, outcome.Tier);
        Assert.Equal(0, outcome.Interval.Start, Tolerance);  // legacy: start forced to 0 (swallows the cold open)
        Assert.Equal(88, outcome.Interval.End, Tolerance);   // latest black frame
    }

    [Fact]
    public void Pipeline_AllTiersDisabled_NoDetection()
    {
        var inputs = ColdOpenWithSubtitlesAndSting();
        var config = new RecapDetectorConfig("none", ChapterEnabled: false, SubtitleEnabled: false, StingEnabled: false, Hardened: true);

        var outcome = RecapTierPipeline.Detect(inputs, config, RecapPhraseMatcher.Default);

        Assert.Equal(RecapTier.None, outcome.Tier);
        Assert.False(outcome.Fired);
    }

    [Fact]
    public void Pipeline_AfterIntroReachedBySubtitleButNotSting()
    {
        // Recap after the OP (intro at 5): the sting window is clamped out, only the subtitle tier reaches it.
        var inputs = new RecapEpisodeInputs
        {
            Duration = 1500, IntroDetected = true, IntroStart = 5,
            StingPresent = true, StingStart = 128, StingEnd = 132,
            BlackFrameTimes = { 128, 160 },
            SubtitleCues =
            {
                new SubtitleCue(129, 132, "Previously on the show..."),
                new SubtitleCue(135, 141, "Everything changed that night."),
                new SubtitleCue(150, 158, "And nothing was ever the same."),
            },
        };

        var hardened = RecapTierPipeline.Detect(inputs, RecapDetectorConfig.HardeningOnly, RecapPhraseMatcher.Default);
        var ensemble = RecapTierPipeline.Detect(inputs, RecapDetectorConfig.Ensemble, RecapPhraseMatcher.Default);

        Assert.False(hardened.Fired);                       // sting cannot reach after-intro
        Assert.Equal(RecapTier.Subtitle, ensemble.Tier);    // subtitle does
        Assert.Equal(128, ensemble.Interval.Start, Tolerance);
        Assert.Equal(160, ensemble.Interval.End, Tolerance);
    }

    // ---- Harm-aware metrics ----

    [Fact]
    public void ContentOutsideTruth_CountsNonRecapSecondsInsideDetection()
    {
        // Baseline cold-open failure: detected [0,88] over a true recap [52,88] => 52 s of cold open skipped.
        Assert.Equal(52.0, RecapMetrics.ContentOutsideTruth(new RecapInterval(0, 88), new RecapInterval(52, 88)), Tolerance);
        Assert.Equal(0.0, RecapMetrics.TruthNotCovered(new RecapInterval(0, 88), new RecapInterval(52, 88)), Tolerance);
        Assert.Equal(0.0, RecapMetrics.ContentOutsideTruth(RecapInterval.Empty, new RecapInterval(52, 88)), Tolerance);
    }

    [Fact]
    public void ItemResult_FiredButWrong_IsHarmfulWithContentSkip()
    {
        var label = new RecapLabel { Series = "X", Season = 1, Episode = 1, HasRecap = true, RecapStart = 52, RecapEnd = 88, SourceShape = RecapSourceShape.ColdOpenThenRecap };
        var result = new RecapItemResult(label, new RecapInterval(0, 88), iouMatchThreshold: 0.5);

        Assert.True(result.IsFiredButWrong);
        Assert.False(result.IsSilentMiss);
        Assert.Equal(52.0, result.ContentSkipSeconds!.Value, Tolerance);
        Assert.Equal(0.0, result.MissedRecapSeconds!.Value, Tolerance);
    }

    [Fact]
    public void ItemResult_SilentMiss_IsSafeWithNoBoundarySample()
    {
        var label = new RecapLabel { Series = "X", Season = 1, Episode = 2, HasRecap = true, RecapStart = 52, RecapEnd = 88, SourceShape = RecapSourceShape.ColdOpenThenRecap };
        var result = new RecapItemResult(label, RecapInterval.Empty, iouMatchThreshold: 0.5);

        Assert.True(result.IsSilentMiss);
        Assert.False(result.IsFiredButWrong);
        Assert.Null(result.ContentSkipSeconds);
    }

    [Fact]
    public void Summary_SplitsFalseNegativeIntoSilentAndFiredButWrong()
    {
        var coldOpen = new RecapLabel { Series = "X", Season = 1, Episode = 1, HasRecap = true, RecapStart = 52, RecapEnd = 88, SourceShape = RecapSourceShape.ColdOpenThenRecap };
        var firstFirst = new RecapLabel { Series = "X", Season = 1, Episode = 2, HasRecap = true, RecapStart = 0, RecapEnd = 30, SourceShape = RecapSourceShape.RecapFirst };

        var results = new[]
        {
            new RecapItemResult(coldOpen, new RecapInterval(0, 88), 0.5), // fired-but-wrong
            new RecapItemResult(firstFirst, RecapInterval.Empty, 0.5),    // silent miss
        };

        var summary = RecapMetricsSummary.FromResults(results);

        Assert.Equal(2, summary.FalseNegatives);
        Assert.Equal(1, summary.FiredButWrong);
        Assert.Equal(1, summary.SilentMisses);
        Assert.Equal(52.0, summary.ContentSkipSecondsTotal, Tolerance);
    }

    // ---- Dataset catalog ----

    [Fact]
    public void Catalog_HasAtLeastThirtyEntriesSpanningAllShapes()
    {
        var scenarios = RecapScenarioCatalog.Default;
        Assert.True(scenarios.Count >= 30, $"expected >= 30 scenarios, got {scenarios.Count}");

        var shapes = scenarios.Select(s => s.Label.SourceShape).Distinct().ToHashSet();
        Assert.Contains(RecapSourceShape.RecapFirst, shapes);
        Assert.Contains(RecapSourceShape.ColdOpenThenRecap, shapes);
        Assert.Contains(RecapSourceShape.AfterIntro, shapes);
        Assert.Contains(RecapSourceShape.NoRecap, shapes);

        Assert.Contains(scenarios, s => s.Label.HasRecap);
        Assert.Contains(scenarios, s => !s.Label.HasRecap);
    }

    [Fact]
    public void Catalog_RoundTripsThroughJson()
    {
        var json = RecapScenarioCatalog.ToSet().Serialize();
        var parsed = RecapScenarioSet.Parse(json);

        Assert.Equal(RecapScenarioCatalog.Default.Count, parsed.Scenarios.Count);
        // A cue with the opener phrase survives serialization (subtitle tier still works after a round trip).
        Assert.Contains(parsed.Scenarios, s => s.Inputs.SubtitleCues.Any(c => c.Text.Contains("Previously on", StringComparison.OrdinalIgnoreCase)));
    }

    // ---- The comparison: relationships are the acceptance check ----

    [Fact]
    public void Comparison_EnsembleMaximizesRecallAndMinimizesHarm()
    {
        var results = RecapComparisonRunner.Run(RecapScenarioCatalog.Default, RecapDetectorConfig.Standard);
        var byName = results.ToDictionary(r => r.Config.Name, r => r.Report.Aggregate, StringComparer.Ordinal);

        var baseline = byName["baseline (shipped)"];
        var hardening = byName["+C hardening"];
        var subtitles = byName["+A subtitles"];
        var ensemble = byName["+A+C ensemble"];

        // Recall: the ensemble is the best (or tied-best) of every configuration.
        Assert.True(ensemble.DetectionRate >= baseline.DetectionRate);
        Assert.True(ensemble.DetectionRate >= hardening.DetectionRate);
        Assert.True(ensemble.DetectionRate >= subtitles.DetectionRate);
        Assert.True(ensemble.DetectionRate > baseline.DetectionRate); // strictly better than shipped

        // False positives: the hardened guard reduces FP vs the shipped baseline; subtitles alone do not.
        Assert.True(hardening.FalsePositiveRate < baseline.FalsePositiveRate);
        Assert.True(ensemble.FalsePositiveRate < baseline.FalsePositiveRate);
        Assert.True(subtitles.FalsePositiveRate >= hardening.FalsePositiveRate);

        // Harm: the shipped baseline skips far more real content than the ensemble, and fires-but-wrong more.
        Assert.True(baseline.ContentSkipSecondsTotal > ensemble.ContentSkipSecondsTotal);
        Assert.True(baseline.ContentSkipSecondsTotal > 100.0); // story-skipping is large in absolute seconds
        Assert.True(ensemble.ContentSkipSecondsTotal < baseline.ContentSkipSecondsTotal / 2.0);
        Assert.True(baseline.FiredButWrong > ensemble.FiredButWrong);

        // Boundary localization improves.
        Assert.True(ensemble.StartMae <= baseline.StartMae);
        Assert.True(ensemble.MeanIoU >= baseline.MeanIoU);
    }

    [Fact]
    public void Comparison_ColdOpenIsWhereBaselineHurtsMost()
    {
        var results = RecapComparisonRunner.Run(RecapScenarioCatalog.Default, RecapDetectorConfig.Standard);

        var baseline = ShapeSummary(results, "baseline (shipped)", RecapSourceShape.ColdOpenThenRecap);
        var ensemble = ShapeSummary(results, "+A+C ensemble", RecapSourceShape.ColdOpenThenRecap);

        // On cold-open recaps the baseline fires-but-wrong (start forced to 0) and skips real content;
        // the ensemble recovers recall and drops the content-skip toward zero.
        Assert.True(ensemble.DetectionRate > baseline.DetectionRate);
        Assert.True(baseline.ContentSkipSecondsTotal > ensemble.ContentSkipSecondsTotal);
        Assert.True(baseline.FiredButWrong >= 1);
    }

    [Fact]
    public void Comparison_WritesArtifactsWhenRequested()
    {
        var results = RecapComparisonRunner.Run(RecapScenarioCatalog.Default, RecapDetectorConfig.Standard);
        var markdown = RecapComparisonRunner.FormatComparison(results);

        Assert.Contains("Recap detector comparison", markdown, StringComparison.Ordinal);
        Assert.Contains("content-skip", markdown, StringComparison.Ordinal);

        var outDir = Environment.GetEnvironmentVariable("RECAP_R2_OUT_DIR");
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "R2-comparison.md"), markdown);
            File.WriteAllText(Path.Combine(outDir, "R2-scenarios.json"), RecapScenarioCatalog.ToSet().Serialize());

            // Also dump each config's full per-shape report and detections for the appendix.
            foreach (var result in results)
            {
                var slug = result.Config.Name.Replace(' ', '_').Replace('(', '_').Replace(')', '_').Replace('+', 'p');
                File.WriteAllText(Path.Combine(outDir, $"report_{slug}.md"), result.Report.Format());
            }
        }
    }

    private static RecapEpisodeInputs ColdOpenWithSubtitlesAndSting() => new()
    {
        Duration = 2600, IntroDetected = true, IntroStart = 92,
        StingPresent = true, StingStart = 52, StingEnd = 56,
        BlackFrameTimes = { 52, 88 },
        SubtitleCues =
        {
            new SubtitleCue(53, 56, "Previously on Tidewater..."),
            new SubtitleCue(58, 63, "The body washed up at dawn."),
            new SubtitleCue(66, 72, "The detective knew the victim."),
            new SubtitleCue(75, 81, "This case is personal now."),
            new SubtitleCue(83, 87, "Everyone is a suspect."),
        },
    };

    private static RecapMetricsSummary ShapeSummary(
        System.Collections.Generic.IReadOnlyList<RecapComparisonRunner.ConfigResult> results,
        string configName,
        RecapSourceShape shape)
    {
        var report = results.Single(r => string.Equals(r.Config.Name, configName, StringComparison.Ordinal)).Report;
        return report.PerShape[shape];
    }
}
