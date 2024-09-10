using System;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

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
