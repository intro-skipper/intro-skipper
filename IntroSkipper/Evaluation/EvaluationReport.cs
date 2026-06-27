// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;

namespace IntroSkipper.Evaluation;

/// <summary>
/// The result of an evaluation run: aggregate metrics, a per-shape breakdown, the raw per-episode
/// results, and a renderer to a human-readable Markdown report.
/// </summary>
internal sealed class EvaluationReport
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EvaluationReport"/> class.
    /// </summary>
    /// <param name="iouMatchThreshold">IoU threshold used to decide a correct match.</param>
    /// <param name="aggregate">Aggregate metrics across every labeled episode.</param>
    /// <param name="perShape">Metrics broken down by <see cref="RecapSourceShape"/>.</param>
    /// <param name="items">The per-episode scored results.</param>
    /// <param name="unmatchedDetections">Detections that did not map to any labeled episode.</param>
    public EvaluationReport(
        double iouMatchThreshold,
        RecapMetricsSummary aggregate,
        IReadOnlyDictionary<RecapSourceShape, RecapMetricsSummary> perShape,
        IReadOnlyList<RecapItemResult> items,
        int unmatchedDetections)
    {
        IouMatchThreshold = iouMatchThreshold;
        Aggregate = aggregate;
        PerShape = perShape;
        Items = items;
        UnmatchedDetections = unmatchedDetections;
    }

    /// <summary>
    /// Gets the IoU threshold used to decide a correct match.
    /// </summary>
    public double IouMatchThreshold { get; }

    /// <summary>
    /// Gets the aggregate metrics across every labeled episode.
    /// </summary>
    public RecapMetricsSummary Aggregate { get; }

    /// <summary>
    /// Gets the metrics broken down by recap shape.
    /// </summary>
    public IReadOnlyDictionary<RecapSourceShape, RecapMetricsSummary> PerShape { get; }

    /// <summary>
    /// Gets the per-episode scored results.
    /// </summary>
    public IReadOnlyList<RecapItemResult> Items { get; }

    /// <summary>
    /// Gets the number of detections that did not map to any labeled episode.
    /// </summary>
    public int UnmatchedDetections { get; }

    /// <summary>
    /// Renders the report as Markdown (aggregate block + per-shape table).
    /// </summary>
    /// <returns>A Markdown document.</returns>
    public string Format()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Recap detection evaluation");
        builder.AppendLine();
        builder.Append("IoU match threshold: ").AppendLine(FormatNumber(IouMatchThreshold));
        builder.AppendLine();

        builder.AppendLine("## Aggregate");
        builder.AppendLine();
        builder.AppendLine("| metric | value |");
        builder.AppendLine("| --- | --- |");
        AppendMetric(builder, "labeled episodes", Count(Aggregate.Total));
        AppendMetric(builder, "with recap", Count(Aggregate.WithRecap));
        AppendMetric(builder, "without recap", Count(Aggregate.WithoutRecap));
        AppendMetric(builder, "true positives", Count(Aggregate.TruePositives));
        AppendMetric(builder, "false positives", Count(Aggregate.FalsePositives));
        AppendMetric(builder, "false negatives", Count(Aggregate.FalseNegatives));
        AppendMetric(builder, "true negatives", Count(Aggregate.TrueNegatives));
        AppendMetric(builder, "detection rate (recall)", RateWithCounts(Aggregate.DetectionRate, Aggregate.TruePositives, Aggregate.WithRecap));
        AppendMetric(builder, "false-positive rate", RateWithCounts(Aggregate.FalsePositiveRate, Aggregate.FalsePositives, Aggregate.WithoutRecap));
        AppendMetric(builder, "precision", FormatRate(Aggregate.Precision));
        AppendMetric(builder, "F1 score", FormatRate(Aggregate.F1Score));
        AppendMetric(builder, "start MAE (s)", SecondsWithCount(Aggregate.StartMae, Aggregate.BoundaryCount));
        AppendMetric(builder, "end MAE (s)", SecondsWithCount(Aggregate.EndMae, Aggregate.BoundaryCount));
        AppendMetric(builder, "mean IoU", FormatRate(Aggregate.MeanIoU));
        AppendMetric(builder, "unmatched detections", Count(UnmatchedDetections));
        builder.AppendLine();

        builder.AppendLine("## Per shape");
        builder.AppendLine();
        builder.AppendLine("| shape | n | withRecap | recall | fpRate | precision | startMAE | endMAE | meanIoU |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var shape in Enum.GetValues<RecapSourceShape>())
        {
            if (!PerShape.TryGetValue(shape, out var summary) || summary.Total == 0)
            {
                continue;
            }

            var row = string.Join(
                " | ",
                shape.ToString(),
                FormatCount(summary.Total),
                FormatCount(summary.WithRecap),
                FormatRate(summary.DetectionRate),
                FormatRate(summary.FalsePositiveRate),
                FormatRate(summary.Precision),
                FormatSeconds(summary.StartMae),
                FormatSeconds(summary.EndMae),
                FormatRate(summary.MeanIoU));
            builder.Append("| ").Append(row).AppendLine(" |");
        }

        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string name, string value)
        => builder.Append("| ").Append(name).Append(" | ").Append(value).AppendLine(" |");

    private static string Count(int value) => FormatCount(value);

    private static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatRate(double value)
        => double.IsNaN(value) ? "n/a" : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string FormatSeconds(double value)
        => double.IsNaN(value) ? "n/a" : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string RateWithCounts(double rate, int numerator, int denominator)
    {
        var num = FormatCount(numerator);
        var den = FormatCount(denominator);
        return string.Concat(FormatRate(rate), " (", num, "/", den, ")");
    }

    private static string SecondsWithCount(double value, int count)
        => string.Concat(FormatSeconds(value), " (n=", FormatCount(count), ")");
}
