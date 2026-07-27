// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Request to include or exclude one episode from media-segment output.
/// </summary>
public sealed class UpdateEpisodeMediaSegmentRequest
{
    /// <summary>
    /// Gets or sets the season identifier.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the episode identifier.
    /// </summary>
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether media-segment output is disabled for the episode.
    /// </summary>
    public bool Disabled { get; set; }
}
