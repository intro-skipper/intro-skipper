// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Marks an item whose automatic segments are withheld from Jellyfin. The row's
/// presence is the flag: only user-provided segments of a disabled item reach the
/// media segment mirror. Analysis and stored segments are unaffected, so removing
/// the row restores the item's segments without re-analysis.
/// </summary>
public sealed class DbDisabledItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbDisabledItem"/> class.
    /// </summary>
    /// <param name="seasonId">Season-state key that owns the item.</param>
    /// <param name="itemId">Item ID.</param>
    public DbDisabledItem(Guid seasonId, Guid itemId)
    {
        SeasonId = seasonId;
        ItemId = itemId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbDisabledItem"/> class.
    /// </summary>
    public DbDisabledItem()
    {
    }

    /// <summary>
    /// Gets or sets the season-state key that owns the item, matching <see cref="DbSeasonState.SeasonId"/>:
    /// the analysis queue's season key for episodes, the item's own ID for movies. Refreshed on
    /// every disable write, so a key gone stale after an item moves seasons heals itself. Used to
    /// clean up rows when the season disappears from the library.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets the item ID.
    /// </summary>
    public Guid ItemId { get; private set; }
}
