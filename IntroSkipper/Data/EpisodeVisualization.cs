// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 rlauuzo
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Episode name and internal ID as returned by the visualization controller.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="EpisodeVisualization"/> class.
/// </remarks>
/// <param name="id">Episode id.</param>
/// <param name="name">Episode name.</param>
public class EpisodeVisualization(Guid id, string name)
{
    /// <summary>
    /// Gets the id.
    /// </summary>
    public Guid Id { get; private set; } = id;

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; private set; } = name;
}
