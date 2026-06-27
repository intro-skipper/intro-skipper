// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// Aggregate metrics computed over a set of <see cref="RecapItemResult"/> items.
/// Rates that are undefined (zero denominator) are reported as <see cref="double.NaN"/>
/// rather than a misleading 0, so callers can distinguish "0%" from "not measurable".
/// </summary>
internal sealed class RecapMetricsSummary
{
    private RecapMetricsSummary()
    {
    }

    /// <summary>
    /// Gets the number of labeled episodes considered.
    /// </summary>
    public int Total { get; private set; }

    /// <summary>
    /// Gets the number of labeled episodes that have a recap (the recall denominator).
    /// </summary>
    public int WithRecap { get; private set; }

    /// <summary>
    /// Gets the number of labeled episodes that have no recap (the false-positive-rate denominator).
    /// </summary>
    public int WithoutRecap { get; private set; }

    /// <summary>
    /// Gets the count of correctly localized recaps.
    /// </summary>
    public int TruePositives { get; private set; }

    /// <summary>
    /// Gets the count of recaps reported on no-recap episodes.
    /// </summary>
    public int FalsePositives { get; private set; }

    /// <summary>
    /// Gets the count of missed or poorly localized recaps.
    /// </summary>
    public int FalseNegatives { get; private set; }

    /// <summary>
    /// Gets the count of no-recap episodes left correctly untouched.
    /// </summary>
    public int TrueNegatives { get; private set; }

    /// <summary>
    /// Gets the number of episodes contributing to the boundary-error averages
    /// (true recap with a firing detection).
    /// </summary>
    public int BoundaryCount { get; private set; }

    /// <summary>
    /// Gets the detection rate (recall on has-recap episodes): TP / (TP + FN).
    /// </summary>
    public double DetectionRate { get; private set; }

    /// <summary>
    /// Gets the false-positive rate on no-recap episodes: FP / (FP + TN).
    /// </summary>
    public double FalsePositiveRate { get; private set; }

    /// <summary>
    /// Gets the precision: TP / (TP + FP).
    /// </summary>
    public double Precision { get; private set; }

    /// <summary>
    /// Gets the F1 score: harmonic mean of precision and recall.
    /// </summary>
    public double F1Score { get; private set; }

    /// <summary>
    /// Gets the mean absolute start-boundary error, in seconds, over <see cref="BoundaryCount"/> episodes.
    /// </summary>
    public double StartMae { get; private set; }

    /// <summary>
    /// Gets the mean absolute end-boundary error, in seconds, over <see cref="BoundaryCount"/> episodes.
    /// </summary>
    public double EndMae { get; private set; }

    /// <summary>
    /// Gets the mean IoU across all has-recap episodes, where a miss contributes 0.
    /// Captures localization and recall in a single number.
    /// </summary>
    public double MeanIoU { get; private set; }

    /// <summary>
    /// Computes a summary from a sequence of scored results.
    /// </summary>
    /// <param name="results">The per-episode results to aggregate.</param>
    /// <returns>The aggregate metrics.</returns>
    public static RecapMetricsSummary FromResults(IEnumerable<RecapItemResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var materialized = results as IReadOnlyList<RecapItemResult> ?? [.. results];
        var summary = new RecapMetricsSummary
        {
            Total = materialized.Count,
        };

        double startErrorSum = 0.0;
        double endErrorSum = 0.0;
        double iouSum = 0.0;

        foreach (var result in materialized)
        {
            if (result.Label.HasRecap)
            {
                summary.WithRecap++;
                iouSum += result.IoU;

                if (result.StartError.HasValue && result.EndError.HasValue)
                {
                    summary.BoundaryCount++;
                    startErrorSum += result.StartError.Value;
                    endErrorSum += result.EndError.Value;
                }
            }
            else
            {
                summary.WithoutRecap++;
            }

            switch (result.Classification)
            {
                case RecapClassification.TruePositive:
                    summary.TruePositives++;
                    break;
                case RecapClassification.FalsePositive:
                    summary.FalsePositives++;
                    break;
                case RecapClassification.FalseNegative:
                    summary.FalseNegatives++;
                    break;
                case RecapClassification.TrueNegative:
                    summary.TrueNegatives++;
                    break;
                default:
                    break;
            }
        }

        summary.DetectionRate = Ratio(summary.TruePositives, summary.WithRecap);
        summary.FalsePositiveRate = Ratio(summary.FalsePositives, summary.WithoutRecap);
        summary.Precision = Ratio(summary.TruePositives, summary.TruePositives + summary.FalsePositives);
        summary.F1Score = HarmonicMean(summary.Precision, summary.DetectionRate);
        summary.StartMae = summary.BoundaryCount > 0 ? startErrorSum / summary.BoundaryCount : double.NaN;
        summary.EndMae = summary.BoundaryCount > 0 ? endErrorSum / summary.BoundaryCount : double.NaN;
        summary.MeanIoU = summary.WithRecap > 0 ? iouSum / summary.WithRecap : double.NaN;

        return summary;
    }

    private static double Ratio(int numerator, int denominator)
        => denominator > 0 ? (double)numerator / denominator : double.NaN;

    private static double HarmonicMean(double precision, double recall)
    {
        if (double.IsNaN(precision) || double.IsNaN(recall) || precision + recall <= 0.0)
        {
            return double.NaN;
        }

        return 2.0 * precision * recall / (precision + recall);
    }
}
