// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Marks an item whose automatic segments are withheld from Jellyfin. The row's
/// presence is the flag: only user-provided segments of a disabled item reach the
/// media segment mirror. Analysis and stored segments are unaffected, so removing
/// the row restores the item's segments without re-analysis. The dashboard lists
/// flags by the items Jellyfin shows under a season, so the row carries no season.
/// </summary>
public sealed class DbDisabledItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbDisabledItem"/> class.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    public DbDisabledItem(Guid itemId)
    {
        ItemId = itemId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbDisabledItem"/> class.
    /// </summary>
    public DbDisabledItem()
    {
    }

    /// <summary>
    /// Gets the item ID.
    /// </summary>
    public Guid ItemId { get; private set; }
}
