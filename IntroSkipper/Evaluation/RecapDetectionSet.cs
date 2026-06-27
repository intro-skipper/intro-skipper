// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// A set of detections produced by one analysis run, scored against a <see cref="RecapDataset"/>.
/// This is the on-disk format the <see cref="RecapEvaluationCommand"/> reads.
/// </summary>
internal sealed class RecapDetectionSet
{
    /// <summary>
    /// Gets or sets the schema version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets the per-episode detections.
    /// </summary>
    public List<RecapDetection> Detections { get; } = [];

    /// <summary>
    /// Parses a detection set from a JSON string.
    /// </summary>
    /// <param name="json">JSON document.</param>
    /// <returns>The parsed detection set, never <see langword="null"/>.</returns>
    public static RecapDetectionSet Parse(string json)
    {
        var set = System.Text.Json.JsonSerializer.Deserialize<RecapDetectionSet>(json, RecapEvaluationJson.Options);
        return set ?? new RecapDetectionSet();
    }

    /// <summary>
    /// Loads a detection set from a JSON file on disk.
    /// </summary>
    /// <param name="path">Path to the detection file.</param>
    /// <returns>The parsed detection set.</returns>
    public static RecapDetectionSet Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Serializes this detection set to an indented JSON string.
    /// </summary>
    /// <returns>The JSON document.</returns>
    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(this, RecapEvaluationJson.Options);
}
