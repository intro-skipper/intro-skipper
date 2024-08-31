using System;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

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
    /// Gets the state of the episode.
    /// </summary>
    public EpisodeState State => Plugin.Instance!.GetState(EpisodeId);

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
}
