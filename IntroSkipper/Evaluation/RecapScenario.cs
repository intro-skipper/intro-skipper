// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// One labeled scenario: the ground-truth <see cref="RecapLabel"/> for an episode plus the
/// synthetic per-tier signal <see cref="RecapEpisodeInputs"/> the detectors run against. Pairing
/// truth and inputs in a single record keeps the dataset self-contained — a config is scored by
/// running <see cref="RecapTierPipeline"/> over <see cref="Inputs"/> and comparing to <see cref="Label"/>.
/// </summary>
internal sealed class RecapScenario
{
    /// <summary>
    /// Gets or sets the ground-truth label (what a human says the recap is).
    /// </summary>
    public RecapLabel Label { get; set; } = new();

    /// <summary>
    /// Gets or sets the per-episode signal inputs (what the detectors get to see).
    /// </summary>
    public RecapEpisodeInputs Inputs { get; set; } = new();
}
