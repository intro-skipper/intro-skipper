// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// A set of labeled scenarios (ground truth + per-tier inputs) used to compare detector
/// configurations. Serialized to and from JSON via <see cref="RecapEvaluationJson.Options"/>, this
/// is the round-2 measurement dataset: it carries everything needed to both run the tiered pipeline
/// and score it. It is SYNTHETIC-REPRESENTATIVE, not real media (see the round-2 measurement doc).
/// </summary>
internal sealed class RecapScenarioSet
{
    /// <summary>
    /// Gets or sets the schema version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets the labeled scenarios.
    /// </summary>
    public List<RecapScenario> Scenarios { get; } = [];

    /// <summary>
    /// Parses a scenario set from a JSON string.
    /// </summary>
    /// <param name="json">JSON document.</param>
    /// <returns>The parsed scenario set, never <see langword="null"/>.</returns>
    public static RecapScenarioSet Parse(string json)
    {
        var set = System.Text.Json.JsonSerializer.Deserialize<RecapScenarioSet>(json, RecapEvaluationJson.Options);
        return set ?? new RecapScenarioSet();
    }

    /// <summary>
    /// Loads a scenario set from a JSON file on disk.
    /// </summary>
    /// <param name="path">Path to the scenario file.</param>
    /// <returns>The parsed scenario set.</returns>
    public static RecapScenarioSet Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Serializes this scenario set to an indented JSON string.
    /// </summary>
    /// <returns>The JSON document.</returns>
    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(this, RecapEvaluationJson.Options);

    /// <summary>
    /// Projects the ground-truth labels into a <see cref="RecapDataset"/> for scoring.
    /// </summary>
    /// <returns>The dataset of labels.</returns>
    public RecapDataset ToDataset()
    {
        var dataset = new RecapDataset { Version = Version };
        foreach (var scenario in Scenarios)
        {
            dataset.Labels.Add(scenario.Label);
        }

        return dataset;
    }
}
