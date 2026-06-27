// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IntroSkipper.Evaluation;
using Xunit;

/// <summary>
/// Exercises the recap ground-truth evaluation harness. Every test is deterministic and
/// media-free: the metric math is fed canned detected-vs-truth pairs. These tests prove the
/// math is correct; they do NOT prove real-world detection accuracy (see D-ensemble-eval.md).
/// </summary>
public class TestRecapEvaluation
{
    private const double Tolerance = 1e-6;

    // ----------------------------------------------------------------------------------
    // Interval geometry: intersection / union / IoU / boundary error.
    // ----------------------------------------------------------------------------------
    [Fact]
    public void Intersection_OverlappingIntervals_ReturnsSharedSeconds()
    {
        Assert.Equal(15.0, RecapMetrics.Intersection(new RecapInterval(0, 30), new RecapInterval(15, 45)), Tolerance);
    }

    [Fact]
    public void Intersection_DisjointIntervals_ReturnsZero()
    {
        Assert.Equal(0.0, RecapMetrics.Intersection(new RecapInterval(0, 10), new RecapInterval(20, 30)), Tolerance);
    }

    [Fact]
    public void Intersection_EmptyInterval_ReturnsZero()
    {
        Assert.Equal(0.0, RecapMetrics.Intersection(RecapInterval.Empty, new RecapInterval(0, 30)), Tolerance);
    }

    [Fact]
    public void Union_OverlappingIntervals_ReturnsCoveredSeconds()
    {
        Assert.Equal(45.0, RecapMetrics.Union(new RecapInterval(0, 30), new RecapInterval(15, 45)), Tolerance);
    }

    [Fact]
    public void IntersectionOverUnion_IdenticalIntervals_IsOne()
    {
        Assert.Equal(1.0, RecapMetrics.IntersectionOverUnion(new RecapInterval(5, 35), new RecapInterval(5, 35)), Tolerance);
    }

    [Fact]
    public void IntersectionOverUnion_KnownIntervals_IsOneThird()
    {
        // intersection = 15, union = 45 -> 1/3.
        Assert.Equal(1.0 / 3.0, RecapMetrics.IntersectionOverUnion(new RecapInterval(0, 30), new RecapInterval(15, 45)), Tolerance);
    }

    [Fact]
    public void IntersectionOverUnion_DisjointOrEmpty_IsZero()
    {
        Assert.Equal(0.0, RecapMetrics.IntersectionOverUnion(new RecapInterval(0, 10), new RecapInterval(20, 30)), Tolerance);
        Assert.Equal(0.0, RecapMetrics.IntersectionOverUnion(RecapInterval.Empty, RecapInterval.Empty), Tolerance);
    }

    [Fact]
    public void BoundaryErrors_ReturnAbsoluteDifferences()
    {
        var detected = new RecapInterval(2, 35);
        var truth = new RecapInterval(0, 30);

        Assert.Equal(2.0, RecapMetrics.AbsoluteStartError(detected, truth), Tolerance);
        Assert.Equal(5.0, RecapMetrics.AbsoluteEndError(detected, truth), Tolerance);
    }

    [Theory]
    [InlineData(0.30, true)]   // IoU = 1/3 ~ 0.333 >= 0.30
    [InlineData(0.50, false)]  // 0.333 < 0.50
    public void IsMatch_RespectsThreshold(double threshold, bool expected)
    {
        var detected = new RecapInterval(0, 30);
        var truth = new RecapInterval(15, 45);

        Assert.Equal(expected, RecapMetrics.IsMatch(detected, truth, threshold));
    }

    // ----------------------------------------------------------------------------------
    // Per-item classification (the confusion matrix).
    // ----------------------------------------------------------------------------------
    [Fact]
    public void ItemResult_HasRecapPerfectDetection_IsTruePositive()
    {
        var label = Label("S", 1, 2, true, 0, 30, RecapSourceShape.RecapFirst);
        var result = new RecapItemResult(label, new RecapInterval(0, 30), 0.5);

        Assert.Equal(RecapClassification.TruePositive, result.Classification);
        Assert.True(result.Fired);
        Assert.Equal(1.0, result.IoU, Tolerance);
        Assert.Equal(0.0, result.StartError!.Value, Tolerance);
        Assert.Equal(0.0, result.EndError!.Value, Tolerance);
    }

    [Fact]
    public void ItemResult_HasRecapNoDetection_IsFalseNegativeWithNullErrors()
    {
        var label = Label("S", 1, 2, true, 0, 30, RecapSourceShape.RecapFirst);
        var result = new RecapItemResult(label, RecapInterval.Empty, 0.5);

        Assert.Equal(RecapClassification.FalseNegative, result.Classification);
        Assert.False(result.Fired);
        Assert.Equal(0.0, result.IoU, Tolerance);
        Assert.Null(result.StartError);
        Assert.Null(result.EndError);
    }

    [Fact]
    public void ItemResult_HasRecapPoorLocalization_IsFalseNegativeButRecordsBoundaryError()
    {
        var label = Label("S", 1, 2, true, 50, 80, RecapSourceShape.ColdOpenThenRecap);

        // Detector fired but at the wrong place (IoU 0): a miss for recall, yet the boundary error
        // is still recorded so we can see how far off it was.
        var result = new RecapItemResult(label, new RecapInterval(0, 30), 0.5);

        Assert.Equal(RecapClassification.FalseNegative, result.Classification);
        Assert.True(result.Fired);
        Assert.Equal(0.0, result.IoU, Tolerance);
        Assert.Equal(50.0, result.StartError!.Value, Tolerance);
        Assert.Equal(50.0, result.EndError!.Value, Tolerance);
    }

    [Fact]
    public void ItemResult_NoRecapDetectionFired_IsFalsePositive()
    {
        var label = Label("S", 1, 4, false, 0, 0, RecapSourceShape.NoRecap);
        var result = new RecapItemResult(label, new RecapInterval(0, 25), 0.5);

        Assert.Equal(RecapClassification.FalsePositive, result.Classification);
        Assert.Null(result.StartError);
    }

    [Fact]
    public void ItemResult_NoRecapNoDetection_IsTrueNegative()
    {
        var label = Label("S", 1, 4, false, 0, 0, RecapSourceShape.NoRecap);
        var result = new RecapItemResult(label, RecapInterval.Empty, 0.5);

        Assert.Equal(RecapClassification.TrueNegative, result.Classification);
    }

    // ----------------------------------------------------------------------------------
    // Aggregate + per-shape runner with hand-computed expectations.
    // ----------------------------------------------------------------------------------
    [Fact]
    public void Evaluate_HandComputedScenario_MatchesExpectedAggregate()
    {
        var report = RecapEvaluator.Evaluate(HandBuiltDataset(), HandBuiltDetections(), new EvaluationOptions { IouMatchThreshold = 0.5 });
        var agg = report.Aggregate;

        Assert.Equal(6, agg.Total);
        Assert.Equal(4, agg.WithRecap);
        Assert.Equal(2, agg.WithoutRecap);
        Assert.Equal(2, agg.TruePositives);
        Assert.Equal(2, agg.FalseNegatives);
        Assert.Equal(1, agg.FalsePositives);
        Assert.Equal(1, agg.TrueNegatives);

        Assert.Equal(0.5, agg.DetectionRate, 4);          // 2 / 4
        Assert.Equal(0.5, agg.FalsePositiveRate, 4);      // 1 / 2
        Assert.Equal(2.0 / 3.0, agg.Precision, 4);        // 2 / 3
        Assert.Equal(3, agg.BoundaryCount);               // fired on 3 of 4 true recaps
        Assert.Equal(50.0 / 3.0, agg.StartMae, 4);        // (0 + 0 + 50) / 3
        Assert.Equal(20.0, agg.EndMae, 4);                // (0 + 10 + 50) / 3
        Assert.Equal((1.0 + (2.0 / 3.0)) / 4.0, agg.MeanIoU, 4); // (1 + .667 + 0 + 0) / 4
    }

    [Fact]
    public void Evaluate_HandComputedScenario_BreaksDownPerShape()
    {
        var report = RecapEvaluator.Evaluate(HandBuiltDataset(), HandBuiltDetections(), new EvaluationOptions { IouMatchThreshold = 0.5 });

        var recapFirst = report.PerShape[RecapSourceShape.RecapFirst];
        Assert.Equal(2, recapFirst.Total);
        Assert.Equal(1.0, recapFirst.DetectionRate, 4);
        Assert.True(double.IsNaN(recapFirst.FalsePositiveRate)); // no no-recap items in this shape

        var coldOpen = report.PerShape[RecapSourceShape.ColdOpenThenRecap];
        Assert.Equal(0.0, coldOpen.DetectionRate, 4);
        Assert.Equal(50.0, coldOpen.StartMae, 4);

        var afterIntro = report.PerShape[RecapSourceShape.AfterIntro];
        Assert.Equal(0.0, afterIntro.DetectionRate, 4);
        Assert.True(double.IsNaN(afterIntro.StartMae)); // never fired -> no boundary samples

        var noRecap = report.PerShape[RecapSourceShape.NoRecap];
        Assert.Equal(0.5, noRecap.FalsePositiveRate, 4);
        Assert.True(double.IsNaN(noRecap.DetectionRate)); // no true recaps in this shape
    }

    [Fact]
    public void Evaluate_CountsUnmatchedDetections()
    {
        var dataset = new RecapDataset();
        dataset.Labels.Add(Label("Show", 1, 2, true, 0, 30, RecapSourceShape.RecapFirst));

        var detections = new List<RecapDetection>
        {
            RecapDetection.FromInterval("Show", 1, 2, new RecapInterval(0, 30)),
            RecapDetection.FromInterval("Show", 1, 99, new RecapInterval(0, 30)), // no label
        };

        var report = RecapEvaluator.Evaluate(dataset, detections);
        Assert.Equal(1, report.UnmatchedDetections);
    }

    [Fact]
    public void Evaluate_JoinsAcrossSeriesCasingAndWhitespace()
    {
        var dataset = new RecapDataset();
        dataset.Labels.Add(Label("Aurora Heights", 1, 2, true, 0, 30, RecapSourceShape.RecapFirst));

        var detections = new List<RecapDetection>
        {
            RecapDetection.FromInterval("  aurora heights  ", 1, 2, new RecapInterval(0, 30)),
        };

        var report = RecapEvaluator.Evaluate(dataset, detections);
        Assert.Equal(1, report.Aggregate.TruePositives);
        Assert.Equal(0, report.UnmatchedDetections);
    }

    // ----------------------------------------------------------------------------------
    // The committed seed dataset, scored by three deterministic synthetic detectors.
    // ----------------------------------------------------------------------------------
    [Fact]
    public void SeedDataset_ParsesAndSpansEveryShape()
    {
        var dataset = LoadSeedDataset();

        Assert.Equal(1, dataset.Version);
        Assert.Equal(14, dataset.Labels.Count);

        var shapes = dataset.Labels.Select(l => l.SourceShape).Distinct().ToHashSet();
        Assert.Contains(RecapSourceShape.RecapFirst, shapes);
        Assert.Contains(RecapSourceShape.ColdOpenThenRecap, shapes);
        Assert.Contains(RecapSourceShape.AfterIntro, shapes);
        Assert.Contains(RecapSourceShape.NoRecap, shapes);

        // Internal consistency of the labels themselves.
        foreach (var label in dataset.Labels)
        {
            if (label.HasRecap)
            {
                Assert.True(label.RecapEnd > label.RecapStart, $"{label.Key} has a non-positive recap span");
                Assert.NotEqual(RecapSourceShape.NoRecap, label.SourceShape);
            }
            else
            {
                Assert.Equal(RecapSourceShape.NoRecap, label.SourceShape);
            }
        }
    }

    [Fact]
    public void SeedDataset_RoundTripsThroughJson()
    {
        var dataset = LoadSeedDataset();
        var reparsed = RecapDataset.Parse(dataset.Serialize());

        Assert.Equal(dataset.Labels.Count, reparsed.Labels.Count);
        Assert.Equal(
            dataset.Labels.Select(l => l.Key).OrderBy(k => k, StringComparer.Ordinal),
            reparsed.Labels.Select(l => l.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void SeedDataset_PerfectDetector_ScoresFlawlessly()
    {
        var dataset = LoadSeedDataset();
        var report = RecapEvaluator.Evaluate(dataset, PerfectDetections(dataset));

        Assert.Equal(1.0, report.Aggregate.DetectionRate, 6);
        Assert.Equal(0.0, report.Aggregate.FalsePositiveRate, 6);
        Assert.Equal(1.0, report.Aggregate.MeanIoU, 6);
        Assert.Equal(0.0, report.Aggregate.StartMae, 6);
        Assert.Equal(0.0, report.Aggregate.EndMae, 6);
        Assert.Equal(0, report.Aggregate.FalseNegatives);
    }

    [Fact]
    public void SeedDataset_SilentDetector_RecallZeroNoFalsePositives()
    {
        var dataset = LoadSeedDataset();
        var report = RecapEvaluator.Evaluate(dataset, Array.Empty<RecapDetection>());

        Assert.Equal(0.0, report.Aggregate.DetectionRate, 6);
        Assert.Equal(0.0, report.Aggregate.FalsePositiveRate, 6);
        Assert.Equal(0.0, report.Aggregate.MeanIoU, 6);
        Assert.Equal(9, report.Aggregate.FalseNegatives);
        Assert.Equal(0, report.Aggregate.FalsePositives);
    }

    [Fact]
    public void SeedDataset_OverEagerDetector_FalsePositiveRateOne()
    {
        var dataset = LoadSeedDataset();
        var report = RecapEvaluator.Evaluate(dataset, OverEagerDetections(dataset));

        Assert.Equal(1.0, report.Aggregate.DetectionRate, 6);
        Assert.Equal(1.0, report.Aggregate.FalsePositiveRate, 6);
        Assert.Equal(5, report.Aggregate.FalsePositives);
    }

    // ----------------------------------------------------------------------------------
    // The thin command entry point (file-in -> report-out).
    // ----------------------------------------------------------------------------------
    [Fact]
    public void Command_WritesMarkdownReport_AndReturnsZero()
    {
        var (truthPath, detectionsPath) = WriteScenarioFiles();
        try
        {
            using var writer = new StringWriter();
            var exit = RecapEvaluationCommand.Execute(["--truth", truthPath, "--detections", detectionsPath], writer);
            var text = writer.ToString();

            Assert.Equal(0, exit);
            Assert.Contains("Recap detection evaluation", text, StringComparison.Ordinal);
            Assert.Contains("detection rate (recall)", text, StringComparison.Ordinal);
            Assert.Contains("RecapFirst", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(truthPath);
            File.Delete(detectionsPath);
        }
    }

    [Fact]
    public void Command_JsonMode_EmitsParseableSummary()
    {
        var (truthPath, detectionsPath) = WriteScenarioFiles();
        try
        {
            using var writer = new StringWriter();
            var exit = RecapEvaluationCommand.Execute(["--truth", truthPath, "--detections", detectionsPath, "--json", "--iou", "0.5"], writer);

            Assert.Equal(0, exit);

            using var doc = JsonDocument.Parse(writer.ToString());
            var aggregate = doc.RootElement.GetProperty("aggregate");
            Assert.Equal(0.5, aggregate.GetProperty("detectionRate").GetDouble(), 4);
            Assert.Equal(0.5, aggregate.GetProperty("falsePositiveRate").GetDouble(), 4);
            Assert.Equal(2, aggregate.GetProperty("truePositives").GetInt32());
        }
        finally
        {
            File.Delete(truthPath);
            File.Delete(detectionsPath);
        }
    }

    [Fact]
    public void Command_MissingArguments_ReturnsUsageError()
    {
        using var writer = new StringWriter();
        var exit = RecapEvaluationCommand.Execute(["--truth", "only-truth.json"], writer);

        Assert.Equal(2, exit);
        Assert.Contains("usage:", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_InvalidIou_ReturnsUsageError()
    {
        using var writer = new StringWriter();
        var exit = RecapEvaluationCommand.Execute(["--truth", "a.json", "--detections", "b.json", "--iou", "abc"], writer);

        Assert.Equal(2, exit);
    }

    [Fact]
    public void Command_MissingFile_ReturnsRuntimeError()
    {
        using var writer = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var exit = RecapEvaluationCommand.Execute(["--truth", missing, "--detections", missing], writer);

        Assert.Equal(1, exit);
        Assert.Contains("error:", writer.ToString(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------
    private static RecapLabel Label(string series, int season, int episode, bool hasRecap, double start, double end, RecapSourceShape shape)
    {
        return new RecapLabel
        {
            Series = series,
            Season = season,
            Episode = episode,
            HasRecap = hasRecap,
            RecapStart = start,
            RecapEnd = end,
            SourceShape = shape,
        };
    }

    private static RecapDataset HandBuiltDataset()
    {
        var dataset = new RecapDataset();
        dataset.Labels.Add(Label("Show", 1, 1, true, 0, 30, RecapSourceShape.RecapFirst));        // L1
        dataset.Labels.Add(Label("Show", 1, 2, true, 0, 20, RecapSourceShape.RecapFirst));        // L2
        dataset.Labels.Add(Label("Show", 1, 3, true, 50, 80, RecapSourceShape.ColdOpenThenRecap)); // L3
        dataset.Labels.Add(Label("Show", 1, 4, true, 100, 130, RecapSourceShape.AfterIntro));     // L4
        dataset.Labels.Add(Label("Show", 1, 5, false, 0, 0, RecapSourceShape.NoRecap));           // L5
        dataset.Labels.Add(Label("Show", 1, 6, false, 0, 0, RecapSourceShape.NoRecap));           // L6
        return dataset;
    }

    private static List<RecapDetection> HandBuiltDetections()
    {
        return new List<RecapDetection>
        {
            RecapDetection.FromInterval("Show", 1, 1, new RecapInterval(0, 30)),   // TP, IoU 1
            RecapDetection.FromInterval("Show", 1, 2, new RecapInterval(0, 30)),   // TP, IoU 2/3, endErr 10
            RecapDetection.FromInterval("Show", 1, 3, new RecapInterval(0, 30)),   // fired but disjoint -> FN
            RecapDetection.FromInterval("Show", 1, 4, RecapInterval.Empty),        // not fired -> FN
            RecapDetection.FromInterval("Show", 1, 5, new RecapInterval(0, 25)),   // FP
            RecapDetection.FromInterval("Show", 1, 6, RecapInterval.Empty),        // TN
        };
    }

    private static List<RecapDetection> PerfectDetections(RecapDataset dataset)
    {
        return dataset.Labels
            .Select(l => RecapDetection.FromInterval(l.Series, l.Season, l.Episode, l.Truth, "oracle"))
            .ToList();
    }

    private static List<RecapDetection> OverEagerDetections(RecapDataset dataset)
    {
        // Fire on everything: exact truth on real recaps, a bogus span on no-recap episodes.
        return dataset.Labels
            .Select(l => RecapDetection.FromInterval(
                l.Series,
                l.Season,
                l.Episode,
                l.HasRecap ? l.Truth : new RecapInterval(0, 20),
                "trigger-happy"))
            .ToList();
    }

    private static (string TruthPath, string DetectionsPath) WriteScenarioFiles()
    {
        var dir = Path.GetTempPath();
        var stamp = Guid.NewGuid().ToString("N");
        var truthPath = Path.Combine(dir, $"recap-truth-{stamp}.json");
        var detectionsPath = Path.Combine(dir, $"recap-detections-{stamp}.json");

        var dataset = HandBuiltDataset();
        var detectionSet = new RecapDetectionSet();
        foreach (var detection in HandBuiltDetections())
        {
            detectionSet.Detections.Add(detection);
        }

        File.WriteAllText(truthPath, dataset.Serialize());
        File.WriteAllText(detectionsPath, detectionSet.Serialize());
        return (truthPath, detectionsPath);
    }

    private static RecapDataset LoadSeedDataset()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "recap-research", "seed-dataset.json");
        Assert.True(File.Exists(path), $"Seed dataset not found at {path}. Check the linked content item in the test .csproj.");
        return RecapDataset.Load(path);
    }
}
