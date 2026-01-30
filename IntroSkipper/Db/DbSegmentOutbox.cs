// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Represents an outbox entry for segment synchronization with Jellyfin.
/// </summary>
public class DbSegmentOutbox
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegmentOutbox"/> class.
    /// </summary>
    public DbSegmentOutbox()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegmentOutbox"/> class.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="type">The analysis mode type.</param>
    /// <param name="segmentIndex">The segment index.</param>
    /// <param name="operation">The operation to perform.</param>
    public DbSegmentOutbox(Guid itemId, AnalysisMode type, int segmentIndex, OutboxOperation operation)
    {
        ItemId = itemId;
        Type = type;
        SegmentIndex = segmentIndex;
        Operation = operation;
        CreatedAt = DateTime.UtcNow;
        RetryCount = 0;
    }

    /// <summary>
    /// Gets or sets the unique identifier for this outbox entry.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the item ID.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the analysis mode type.
    /// </summary>
    public AnalysisMode Type { get; set; }

    /// <summary>
    /// Gets or sets the segment index.
    /// </summary>
    public int SegmentIndex { get; set; }

    /// <summary>
    /// Gets or sets the operation to perform.
    /// </summary>
    public OutboxOperation Operation { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this entry was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the number of retry attempts.
    /// </summary>
    public int RetryCount { get; set; }
}
