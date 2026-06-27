// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;

namespace IntroSkipper.Evaluation;

/// <summary>
/// A thin, opt-in entry point that scores a detections file against a ground-truth dataset file
/// and writes a report. Nothing in the production analysis path calls this; it exists so the
/// harness can be exercised from a test, a future scheduled-task Command, or a tiny console shim.
/// </summary>
/// <remarks>
/// To wire this to a real analysis run, export one <see cref="RecapDetection"/> per labeled episode
/// from the plugin database (the Recap segment returned by <c>Plugin.GetSegmentsAsync(itemId)</c>,
/// mapped to <c>Detected = segment.Valid</c>, <c>DetectedStart/End = segment.Start/End</c>), write it
/// as a <see cref="RecapDetectionSet"/> JSON file, and run this command against the label file.
/// </remarks>
internal static class RecapEvaluationCommand
{
    /// <summary>
    /// Usage text describing the accepted arguments.
    /// </summary>
    public const string Usage =
        "usage: recap-eval --truth <labels.json> --detections <detections.json> [--iou <0..1>] [--json]";

    /// <summary>
    /// Runs the evaluation.
    /// </summary>
    /// <param name="args">Command arguments.</param>
    /// <param name="output">Destination for the report or error text.</param>
    /// <returns>0 on success, 2 on a usage error, 1 on a runtime error.</returns>
    public static int Execute(IReadOnlyList<string> args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? truthPath = null;
        string? detectionsPath = null;
        var emitJson = false;
        var options = new EvaluationOptions();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--truth":
                case "--detections":
                case "--iou":
                    if (i + 1 >= args.Count)
                    {
                        return Fail(output, string.Concat("missing value for ", arg));
                    }

                    var value = args[++i];
                    if (arg == "--truth")
                    {
                        truthPath = value;
                    }
                    else if (arg == "--detections")
                    {
                        detectionsPath = value;
                    }
                    else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var iou))
                    {
                        return Fail(output, string.Concat("invalid --iou value: ", value));
                    }
                    else
                    {
                        options.IouMatchThreshold = iou;
                    }

                    break;
                case "--json":
                    emitJson = true;
                    break;
                default:
                    return Fail(output, string.Concat("unknown argument: ", arg));
            }
        }

        if (string.IsNullOrWhiteSpace(truthPath) || string.IsNullOrWhiteSpace(detectionsPath))
        {
            return Fail(output, "both --truth and --detections are required");
        }

        try
        {
            var dataset = RecapDataset.Load(truthPath);
            var detectionSet = RecapDetectionSet.Load(detectionsPath);
            var report = RecapEvaluator.Evaluate(dataset, detectionSet.Detections, options);

            output.Write(emitJson ? ToJson(report) : report.Format());
            return 0;
        }
        catch (IOException ex)
        {
            return FailRuntime(output, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return FailRuntime(output, ex);
        }
        catch (JsonException ex)
        {
            return FailRuntime(output, ex);
        }
        catch (ArgumentException ex)
        {
            return FailRuntime(output, ex);
        }
    }

    /// <summary>
    /// Serializes the report's metrics as compact JSON (undefined rates become <c>null</c>).
    /// </summary>
    /// <param name="report">The report to serialize.</param>
    /// <returns>A JSON document.</returns>
    public static string ToJson(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iouMatchThreshold"] = report.IouMatchThreshold,
            ["unmatchedDetections"] = report.UnmatchedDetections,
            ["aggregate"] = ToMap(report.Aggregate),
            ["perShape"] = report.PerShape
                .Where(kvp => kvp.Value.Total > 0)
                .OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key.ToString(), kvp => (object?)ToMap(kvp.Value), StringComparer.Ordinal),
        };

        return JsonSerializer.Serialize(payload, RecapEvaluationJson.Options);
    }

    private static Dictionary<string, object?> ToMap(RecapMetricsSummary summary)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total"] = summary.Total,
            ["withRecap"] = summary.WithRecap,
            ["withoutRecap"] = summary.WithoutRecap,
            ["truePositives"] = summary.TruePositives,
            ["falsePositives"] = summary.FalsePositives,
            ["falseNegatives"] = summary.FalseNegatives,
            ["silentMisses"] = summary.SilentMisses,
            ["firedButWrong"] = summary.FiredButWrong,
            ["trueNegatives"] = summary.TrueNegatives,
            ["detectionRate"] = OrNull(summary.DetectionRate),
            ["falsePositiveRate"] = OrNull(summary.FalsePositiveRate),
            ["precision"] = OrNull(summary.Precision),
            ["f1Score"] = OrNull(summary.F1Score),
            ["contentSkipSecondsTotal"] = summary.ContentSkipSecondsTotal,
            ["contentSkipSecondsMean"] = OrNull(summary.ContentSkipSecondsMean),
            ["missedRecapSecondsTotal"] = summary.MissedRecapSecondsTotal,
            ["missedRecapSecondsMean"] = OrNull(summary.MissedRecapSecondsMean),
            ["startMae"] = OrNull(summary.StartMae),
            ["endMae"] = OrNull(summary.EndMae),
            ["meanIoU"] = OrNull(summary.MeanIoU),
            ["boundaryCount"] = summary.BoundaryCount,
        };
    }

    private static object? OrNull(double value) => double.IsNaN(value) ? null : value;

    private static int Fail(TextWriter output, string message)
    {
        output.Write(string.Concat("error: ", message, Environment.NewLine, Usage, Environment.NewLine));
        return 2;
    }

    private static int FailRuntime(TextWriter output, Exception ex)
    {
        output.Write(string.Concat("error: ", ex.Message, Environment.NewLine));
        return 1;
    }
}
