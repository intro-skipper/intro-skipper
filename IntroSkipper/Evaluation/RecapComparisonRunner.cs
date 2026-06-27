// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using IntroSkipper.Subtitles;

namespace IntroSkipper.Evaluation;

/// <summary>
/// Runs every detector configuration over a labeled scenario set and renders one side-by-side
/// comparison report. This is the round-2 deliverable's engine: the numbers are produced by actually
/// running <see cref="RecapTierPipeline"/> (which calls the real spike A/C logic) and scoring its
/// output through the existing <see cref="RecapEvaluator"/> — no metric is asserted by hand.
/// </summary>
internal static class RecapComparisonRunner
{
    /// <summary>
    /// Runs the configurations over the scenarios and scores each one.
    /// </summary>
    /// <param name="scenarios">The labeled scenarios (truth + inputs).</param>
    /// <param name="configs">The configurations to compare.</param>
    /// <param name="iouThreshold">IoU match threshold (default 0.5).</param>
    /// <param name="matcher">Phrase matcher for the subtitle tier (defaults to the curated multilingual set).</param>
    /// <returns>The scored result per configuration, in input order.</returns>
    public static IReadOnlyList<ConfigResult> Run(
        IReadOnlyList<RecapScenario> scenarios,
        IReadOnlyList<RecapDetectorConfig> configs,
        double iouThreshold = 0.5,
        RecapPhraseMatcher? matcher = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(configs);

        var effectiveMatcher = matcher ?? RecapPhraseMatcher.Default;
        var dataset = new RecapDataset();
        foreach (var scenario in scenarios)
        {
            dataset.Labels.Add(scenario.Label);
        }

        var options = new EvaluationOptions { IouMatchThreshold = iouThreshold };
        var results = new List<ConfigResult>(configs.Count);
        foreach (var config in configs)
        {
            var detections = new RecapDetectionSet();
            foreach (var scenario in scenarios)
            {
                var outcome = RecapTierPipeline.Detect(scenario.Inputs, config, effectiveMatcher);
                detections.Detections.Add(RecapDetection.FromInterval(
                    scenario.Label.Series,
                    scenario.Label.Season,
                    scenario.Label.Episode,
                    outcome.Interval,
                    outcome.Fired ? outcome.Signal : null));
            }

            var report = RecapEvaluator.Evaluate(dataset, detections.Detections, options);
            results.Add(new ConfigResult(config, report, detections));
        }

        return results;
    }

    /// <summary>
    /// Renders the comparison as Markdown: an aggregate metric-by-config table followed by a
    /// per-shape table for each source shape present in the dataset.
    /// </summary>
    /// <param name="results">The per-config scored results (from <see cref="Run"/>).</param>
    /// <returns>A Markdown document.</returns>
    public static string FormatComparison(IReadOnlyList<ConfigResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return "(no configurations)";
        }

        var threshold = results[0].Report.IouMatchThreshold;
        var builder = new StringBuilder();
        builder.AppendLine("# Recap detector comparison");
        builder.AppendLine();
        builder.Append("IoU match threshold: ").AppendLine(FormatNumber(threshold));
        builder.AppendLine();

        var first = results[0].Report.Aggregate;
        builder.Append("Dataset: ").Append(first.Total.ToString(CultureInfo.InvariantCulture))
            .Append(" labeled episodes (").Append(first.WithRecap.ToString(CultureInfo.InvariantCulture))
            .Append(" with recap, ").Append(first.WithoutRecap.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" without).");
        builder.AppendLine();

        builder.AppendLine("## Aggregate");
        builder.AppendLine();
        AppendComparisonTable(builder, results, r => r.Aggregate);

        builder.AppendLine();
        builder.AppendLine("## Per shape");
        builder.AppendLine();
        foreach (var shape in Enum.GetValues<RecapSourceShape>())
        {
            if (!AnyShape(results, shape))
            {
                continue;
            }

            builder.Append("### ").AppendLine(shape.ToString());
            builder.AppendLine();
            AppendComparisonTable(builder, results, r => r.PerShape.TryGetValue(shape, out var s) ? s : null);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static bool AnyShape(IReadOnlyList<ConfigResult> results, RecapSourceShape shape)
        => results[0].Report.PerShape.TryGetValue(shape, out var summary) && summary.Total > 0;

    private static void AppendComparisonTable(
        StringBuilder builder,
        IReadOnlyList<ConfigResult> results,
        Func<EvaluationReport, RecapMetricsSummary?> select)
    {
        builder.Append("| metric");
        foreach (var result in results)
        {
            builder.Append(" | ").Append(result.Config.Name);
        }

        builder.AppendLine(" |");

        builder.Append("| ---");
        for (var i = 0; i < results.Count; i++)
        {
            builder.Append(" | ---");
        }

        builder.AppendLine(" |");

        var summaries = results.Select(r => select(r.Report)).ToArray();

        AppendRow(builder, "n (episodes)", summaries, s => Count(s.Total));
        AppendRow(builder, "with recap", summaries, s => Count(s.WithRecap));
        AppendRow(builder, "without recap", summaries, s => Count(s.WithoutRecap));
        AppendRow(builder, "detection rate (recall)", summaries, s => RateWithCounts(s.DetectionRate, s.TruePositives, s.WithRecap));
        AppendRow(builder, "false-positive rate", summaries, s => RateWithCounts(s.FalsePositiveRate, s.FalsePositives, s.WithoutRecap));
        AppendRow(builder, "fired-but-wrong (harmful)", summaries, s => Count(s.FiredButWrong));
        AppendRow(builder, "silent miss (safe)", summaries, s => Count(s.SilentMisses));
        AppendRow(builder, "content-skip s — total (harm)", summaries, s => FormatSeconds(s.ContentSkipSecondsTotal));
        AppendRow(builder, "content-skip s — mean/fired", summaries, s => SecondsWithCount(s.ContentSkipSecondsMean, s.BoundaryCount));
        AppendRow(builder, "missed-recap s — total", summaries, s => FormatSeconds(s.MissedRecapSecondsTotal));
        AppendRow(builder, "precision", summaries, s => FormatRate(s.Precision));
        AppendRow(builder, "F1 score", summaries, s => FormatRate(s.F1Score));
        AppendRow(builder, "start MAE (s)", summaries, s => SecondsWithCount(s.StartMae, s.BoundaryCount));
        AppendRow(builder, "end MAE (s)", summaries, s => SecondsWithCount(s.EndMae, s.BoundaryCount));
        AppendRow(builder, "mean IoU", summaries, s => FormatRate(s.MeanIoU));
    }

    private static void AppendRow(
        StringBuilder builder,
        string metric,
        IReadOnlyList<RecapMetricsSummary?> summaries,
        Func<RecapMetricsSummary, string> render)
    {
        builder.Append("| ").Append(metric);
        foreach (var summary in summaries)
        {
            builder.Append(" | ").Append(summary is null ? "n/a" : render(summary));
        }

        builder.AppendLine(" |");
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatRate(double value)
        => double.IsNaN(value) ? "n/a" : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string FormatSeconds(double value)
        => double.IsNaN(value) ? "n/a" : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string RateWithCounts(double rate, int numerator, int denominator)
        => string.Concat(FormatRate(rate), " (", Count(numerator), "/", Count(denominator), ")");

    private static string SecondsWithCount(double value, int count)
        => string.Concat(FormatSeconds(value), " (n=", Count(count), ")");

    /// <summary>
    /// The scored result for a single configuration.
    /// </summary>
    /// <param name="Config">The configuration that was run.</param>
    /// <param name="Report">The evaluation report produced by scoring its detections.</param>
    /// <param name="Detections">The detections the pipeline produced for this configuration.</param>
    public sealed record ConfigResult(RecapDetectorConfig Config, EvaluationReport Report, RecapDetectionSet Detections);
}
