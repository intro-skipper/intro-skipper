// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Records that an item was analyzed for one mode under a configuration hash, whether
/// or not segments were found. Queue verification settles an item whose record matches
/// the current hash (<c>Analyzed</c> with segments, <c>NoSegments</c> without) and
/// re-analyzes it otherwise; deleting the record reopens the item for the mode. One row
/// per (item, mode). The file version ties the record to the media file Jellyfin held
/// at analysis time: a record whose version differs from the item's current one no
/// longer describes the file and is re-analyzed. A null version makes no claim and
/// matches any file (rows written before versions were recorded).
/// </summary>
public sealed class DbAnalyzedItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbAnalyzedItem"/> class.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="type">Analysis mode.</param>
    /// <param name="configHash">Configuration hash the item was analyzed under.</param>
    public DbAnalyzedItem(Guid itemId, AnalysisMode type, string configHash)
    {
        ItemId = itemId;
        Type = type;
        ConfigHash = configHash;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbAnalyzedItem"/> class.
    /// </summary>
    public DbAnalyzedItem()
    {
    }

    /// <summary>
    /// Gets the item id.
    /// </summary>
    public Guid ItemId { get; private set; }

    /// <summary>
    /// Gets the analysis mode.
    /// </summary>
    public AnalysisMode Type { get; private set; }

    /// <summary>
    /// Gets the configuration hash the item was analyzed under. The facade rewrites it
    /// with a set-based upsert, never through a tracked entity.
    /// </summary>
    public string ConfigHash { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the file version the item was analyzed at: the ticks of the last write time
    /// Jellyfin held for the media file. Null when the record predates versioning or
    /// Jellyfin held no write time. Written only by the facade's set-based statements.
    /// </summary>
    public long? FileVersion { get; private set; }
}
