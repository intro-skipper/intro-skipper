// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Outbox pattern entity for tracking segment operations that need to sync to Jellyfin.
/// </summary>
public class DbSegmentOutbox
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the item (episode/movie) id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the operation type.
    /// </summary>
    public OutboxOperation Operation { get; set; }

    /// <summary>
    /// Gets or sets the segment id.
    /// For upsert operations, this is the ID of the created/updated segment.
    /// For delete operations, this is null because the segment no longer exists;
    /// the outbox processor will trigger a full refresh from the provider for the item.
    /// </summary>
    public int? SegmentId { get; set; }

    /// <summary>
    /// Gets or sets when this entry was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the retry count.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets when this entry was processed.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Gets or sets the error message if processing failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the instance ID that has claimed this entry for processing.
    /// Null if the entry is not claimed.
    /// </summary>
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// Gets or sets when this entry was claimed for processing.
    /// Used to detect stale claims from crashed processors.
    /// </summary>
    public DateTime? ClaimedAt { get; set; }
}
