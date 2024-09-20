using System;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Controllers;

/// <summary>
/// Repräsentiert die Visualisierung einer Episode.
/// </summary>
/// <param name="episodeId">Die eindeutige ID der Episode.</param>
/// <param name="showName">Der Name der Show.</param>
/// <param name="episodeName">Der Name der Episode.</param>
public class EpisodeVisualization(Guid episodeId, string showName, string episodeName)
{
    /// <summary>
    /// Gets or sets the ID of the episode.
    /// </summary>
    public Guid EpisodeId { get; set; } = episodeId;

    /// <summary>
    /// Gets or sets the name of the show.
    /// </summary>
    public string ShowName { get; set; } = showName;

    /// <summary>
    /// Gets or sets the name of the episode.
    /// </summary>
    public string EpisodeName { get; set; } = episodeName;
}
