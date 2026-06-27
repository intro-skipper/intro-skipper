// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// A labeled dataset: the ground truth a detection run is scored against.
/// Serialized to and from JSON via <see cref="RecapEvaluationJson.Options"/>.
/// </summary>
internal sealed class RecapDataset
{
    /// <summary>
    /// Gets or sets the dataset schema version. Bumped if the on-disk shape changes.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets the labeled episodes.
    /// </summary>
    public List<RecapLabel> Labels { get; } = [];

    /// <summary>
    /// Parses a dataset from a JSON string.
    /// </summary>
    /// <param name="json">JSON document.</param>
    /// <returns>The parsed dataset, never <see langword="null"/>.</returns>
    public static RecapDataset Parse(string json)
    {
        var dataset = System.Text.Json.JsonSerializer.Deserialize<RecapDataset>(json, RecapEvaluationJson.Options);
        return dataset ?? new RecapDataset();
    }

    /// <summary>
    /// Loads a dataset from a JSON file on disk.
    /// </summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <returns>The parsed dataset.</returns>
    public static RecapDataset Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Serializes this dataset to an indented JSON string.
    /// </summary>
    /// <returns>The JSON document.</returns>
    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(this, RecapEvaluationJson.Options);
}
