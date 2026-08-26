// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Records that an item was analyzed for one mode under a configuration hash, whether
/// or not segments were found. Queue verification settles an item whose record matches
/// the current hash (<c>Analyzed</c> with segments, <c>NoSegments</c> without) and
/// re-analyzes it otherwise; deleting the record reopens the item for the mode. One row
/// per (item, mode).
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
    /// Gets the configuration hash the item was analyzed under. Records are replaced, never
    /// updated in place: the facade's upsert is a delete-then-insert inside one transaction.
    /// </summary>
    public string ConfigHash { get; private set; } = string.Empty;
}
