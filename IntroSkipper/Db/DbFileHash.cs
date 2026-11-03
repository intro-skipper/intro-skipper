// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Records an item's file hash, the season key it was analyzed under and the file version
/// the hash was computed at. The hash outlives the item id: when the server forgets an id
/// after a rename or a changed library path, the live item with the same hash takes over
/// its rows (<see cref="IntroSkipperDatabase.RebindItemsAsync"/>). One row per item.
/// </summary>
public sealed class DbFileHash
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbFileHash"/> class.
    /// </summary>
    /// <param name="itemId">The item (episode or movie) id.</param>
    /// <param name="seasonId">The season key the item is analyzed under.</param>
    /// <param name="hash">The file hash.</param>
    /// <param name="fileVersion">The file version the hash was computed at.</param>
    public DbFileHash(Guid itemId, Guid seasonId, string hash, long? fileVersion)
    {
        ItemId = itemId;
        SeasonId = seasonId;
        Hash = hash;
        FileVersion = fileVersion;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbFileHash"/> class.
    /// </summary>
    public DbFileHash()
    {
    }

    /// <summary>
    /// Gets the item id.
    /// </summary>
    public Guid ItemId { get; private set; }

    /// <summary>
    /// Gets the season key the item was last analyzed under, so a rebind can carry the
    /// season's state when the season's own id changed too.
    /// </summary>
    public Guid SeasonId { get; private set; }

    /// <summary>
    /// Gets the file hash (<see cref="Helper.FileHash"/>).
    /// </summary>
    public string Hash { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the file version the hash was computed at. A different current version means
    /// the file may have changed, and the hash is computed again.
    /// </summary>
    public long? FileVersion { get; private set; }
}
