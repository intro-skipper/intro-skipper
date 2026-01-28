// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Constants for outbox processing configuration.
/// </summary>
public static class OutboxConstants
{
    /// <summary>
    /// Maximum number of retry attempts before an outbox entry is considered failed.
    /// </summary>
    public const int MaxRetryCount = 5;

    /// <summary>
    /// Number of outbox entries to process in each batch.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Interval between outbox polling cycles.
    /// </summary>
    public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before retrying after an error in the processor loop.
    /// </summary>
    public static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long processed entries are retained before cleanup.
    /// </summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// Timeout for claiming outbox entries. Entries claimed longer than this are considered stale.
    /// </summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);
}
