// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Episode queued for analysis.
/// </summary>
public class QueuedEpisode
{
    private readonly bool[] _isAnalyzed = new bool[4];

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// Gets a value indicating whether this media has been already analyzed.
    /// </summary>
    public IReadOnlyList<bool> IsAnalyzed => _isAnalyzed;

    /// <summary>
    /// Gets or sets the full path to episode.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the episode.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether an episode is Anime.
    /// </summary>
    public bool IsAnime { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an item is a movie.
    /// </summary>
    public bool IsMovie { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to stop searching for an introduction at.
    /// </summary>
    public int IntroFingerprintEnd { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to start looking for end credits at.
    /// </summary>
    public int CreditsFingerprintStart { get; set; }

    /// <summary>
    /// Gets or sets the total duration of this media file (in seconds).
    /// </summary>
    public int Duration { get; set; }

    /// <summary>
    /// Sets a value indicating whether this media has been already analyzed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="value">Value to set.</param>
    public void SetAnalyzed(AnalysisMode mode, bool value) => _isAnalyzed[(int)mode] = value;

    /// <summary>
    /// Gets a value indicating whether this media has been already analyzed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Value of the analyzed mode.</returns>
    public bool GetAnalyzed(AnalysisMode mode) => _isAnalyzed[(int)mode];
}
