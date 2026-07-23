// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Request to enable or disable analysis for one episode.
/// </summary>
public sealed class UpdateEpisodeAnalysisRequest
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
    /// Gets or sets a value indicating whether the episode is disabled.
    /// </summary>
    public bool Disabled { get; set; }
}
