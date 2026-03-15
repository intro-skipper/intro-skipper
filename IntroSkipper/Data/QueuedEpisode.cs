// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Episode queued for analysis.
/// </summary>
public class QueuedEpisode
{
    private readonly EpisodeState[] _isAnalyzed = new EpisodeState[Enum.GetValues<AnalysisMode>().Length];

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the season id.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// Gets a value indicating whether this media has been already analyzed.
    /// </summary>
    public IReadOnlyList<EpisodeState> IsAnalyzed => _isAnalyzed;

    /// <summary>
    /// Gets or sets the full path to episode.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the episode.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category for this media item.
    /// </summary>
    public QueuedMediaCategory Category { get; set; } = QueuedMediaCategory.Episode;

    /// <summary>
    /// Gets or sets a value indicating whether this media item should be excluded from analysis.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to stop searching for an introduction at.
    /// </summary>
    public double IntroFingerprintEnd { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to start looking for end credits at.
    /// </summary>
    public double CreditsFingerprintStart { get; set; }

    /// <summary>
    /// Gets or sets the total duration of this media file (in seconds).
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Sets a value indicating whether this media has been already analyzed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="value">Value to set.</param>
    public void SetAnalyzed(AnalysisMode mode, EpisodeState value)
    {
        _isAnalyzed[(int)mode] = value;
    }

    /// <summary>
    /// Sets a value indicating whether this media has been already analyzed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Value of the analyzed mode.</returns>
    public EpisodeState GetAnalyzed(AnalysisMode mode)
    {
        return _isAnalyzed[(int)mode];
    }
}
