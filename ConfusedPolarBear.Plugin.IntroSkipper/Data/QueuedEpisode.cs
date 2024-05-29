using System;
using System.Collections.Generic;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Episode queued for analysis.
/// </summary>
public class QueuedEpisode
{
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
    /// Gets a set of AnalysisMode flags indicating which analysis modes have been performed on this episode.
    /// </summary>
    public HashSet<AnalysisMode> IsAnalyzed { get; } = new HashSet<AnalysisMode>();

    /// <summary>
    /// Gets a set of AnalysisMode flags indicating which analysis modes have been performed on this episode.
    /// </summary>
    public HashSet<AnalysisMode> IsBlacklisted { get; } = new HashSet<AnalysisMode>();

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
    public bool IsAnime { get; set; } = false;

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
    /// Initializes a new instance of the <see cref="QueuedEpisode"/> class.
    /// </summary>
    /// <param name="mode">AnalysisMode.</param>
    public void AddAnalysisMode(AnalysisMode mode) => IsAnalyzed.Add(mode);

    /// <summary>
    /// Initializes a new instance of the <see cref="QueuedEpisode"/> class.
    /// </summary>
    /// <param name="mode">AnalysisMode.</param>
    public void AddBlacklistMode(AnalysisMode mode) => IsBlacklisted.Add(mode);

    /// <summary>
    /// Initializes a new instance of the <see cref="QueuedEpisode"/> class.
    /// </summary>
    /// <param name="mode">AnalysisMode.</param>
    public void RemoveBlacklistMode(AnalysisMode mode) => IsBlacklisted.Remove(mode);
}
