// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace IntroSkipper.Evaluation;

/// <summary>
/// A single ground-truth label for one episode. This is the unit of the labeled dataset
/// (<see cref="RecapDataset"/>). Boundaries are in seconds from the start of the file.
/// </summary>
internal sealed class RecapLabel
{
    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string Series { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int Season { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int Episode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the episode contains a recap.
    /// </summary>
    public bool HasRecap { get; set; }

    /// <summary>
    /// Gets or sets the recap start, in seconds. Ignored when <see cref="HasRecap"/> is <see langword="false"/>.
    /// </summary>
    public double RecapStart { get; set; }

    /// <summary>
    /// Gets or sets the recap end, in seconds. Ignored when <see cref="HasRecap"/> is <see langword="false"/>.
    /// </summary>
    public double RecapEnd { get; set; }

    /// <summary>
    /// Gets or sets the structural placement of the recap.
    /// </summary>
    public RecapSourceShape SourceShape { get; set; } = RecapSourceShape.Unknown;

    /// <summary>
    /// Gets or sets free-form notes describing the source or any caveats for this label.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Gets the ground-truth interval, or an empty interval when the episode has no recap.
    /// </summary>
    [JsonIgnore]
    public RecapInterval Truth => HasRecap ? new RecapInterval(RecapStart, RecapEnd) : RecapInterval.Empty;

    /// <summary>
    /// Gets the normalized join key for this label.
    /// </summary>
    [JsonIgnore]
    public string Key => RecapEpisodeKey.For(Series, Season, Episode);
}
