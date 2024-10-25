// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

using System;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Episode name and internal ID as returned by the visualization controller.
/// </summary>
public class EpisodeVisualization
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeVisualization"/> class.
    /// </summary>
    /// <param name="id">Episode id.</param>
    /// <param name="name">Episode name.</param>
    public EpisodeVisualization(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>
    /// Gets the id.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; private set; } = string.Empty;
}
