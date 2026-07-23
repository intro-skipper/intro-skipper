// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Stores an episode that is excluded from media-segment output.
/// </summary>
public sealed class DbDisabledEpisode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbDisabledEpisode"/> class.
    /// </summary>
    /// <param name="seasonId">Season identifier.</param>
    /// <param name="episodeId">Episode identifier.</param>
    public DbDisabledEpisode(Guid seasonId, Guid episodeId)
    {
        SeasonId = seasonId;
        EpisodeId = episodeId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbDisabledEpisode"/> class.
    /// </summary>
    public DbDisabledEpisode()
    {
    }

    /// <summary>
    /// Gets the season identifier.
    /// </summary>
    public Guid SeasonId { get; private set; }

    /// <summary>
    /// Gets the episode identifier.
    /// </summary>
    public Guid EpisodeId { get; private set; }
}
