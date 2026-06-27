// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// The runner: joins detections to ground-truth labels, scores each episode, and produces an
/// <see cref="EvaluationReport"/>. Pure and media-free — fed canned detected-vs-truth pairs in tests.
/// </summary>
internal static class RecapEvaluator
{
    /// <summary>
    /// Evaluates a set of detections against a labeled dataset.
    /// </summary>
    /// <param name="dataset">The ground-truth labels.</param>
    /// <param name="detections">The detections to score (one analysis run).</param>
    /// <param name="options">Evaluation options; defaults are used when <see langword="null"/>.</param>
    /// <returns>The evaluation report.</returns>
    public static EvaluationReport Evaluate(
        RecapDataset dataset,
        IEnumerable<RecapDetection> detections,
        EvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(detections);

        var effectiveOptions = options ?? new EvaluationOptions();
        var threshold = Math.Clamp(effectiveOptions.IouMatchThreshold, 0.0, 1.0);

        // Index detections by join key. When an episode has more than one detection row, keep a
        // firing one over a non-firing duplicate so a real export with stray empty rows still scores.
        var detectionLookup = new Dictionary<string, RecapDetection>(StringComparer.Ordinal);
        foreach (var detection in detections)
        {
            var key = detection.Key;
            if (!detectionLookup.TryGetValue(key, out var existing) || (!existing.Detected && detection.Detected))
            {
                detectionLookup[key] = detection;
            }
        }

        var labelKeys = new HashSet<string>(dataset.Labels.Select(label => label.Key), StringComparer.Ordinal);
        var unmatchedDetections = detectionLookup.Keys.Count(key => !labelKeys.Contains(key));

        var results = new List<RecapItemResult>(dataset.Labels.Count);
        foreach (var label in dataset.Labels)
        {
            var interval = detectionLookup.TryGetValue(label.Key, out var detection)
                ? detection.Interval
                : RecapInterval.Empty;
            results.Add(new RecapItemResult(label, interval, threshold));
        }

        var perShape = results
            .GroupBy(result => result.Label.SourceShape)
            .ToDictionary(group => group.Key, RecapMetricsSummary.FromResults);

        var aggregate = RecapMetricsSummary.FromResults(results);

        return new EvaluationReport(threshold, aggregate, perShape, results, unmatchedDetections);
    }
}
