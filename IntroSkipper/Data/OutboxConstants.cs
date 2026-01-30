// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Constants used for the outbox pattern in segment synchronization.
/// </summary>
public static class OutboxConstants
{
    /// <summary>
    /// Maximum number of retry attempts for failed outbox entries.
    /// </summary>
    public const int MaxRetryAttempts = 5;

    /// <summary>
    /// Default batch size for processing outbox entries.
    /// </summary>
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// Initial delay in seconds before the first retry.
    /// </summary>
    public const int InitialRetryDelaySeconds = 1;

    /// <summary>
    /// Maximum delay in seconds between retries.
    /// </summary>
    public const int MaxRetryDelaySeconds = 300;

    /// <summary>
    /// Multiplier for exponential backoff.
    /// </summary>
    public const double BackoffMultiplier = 2.0;

    /// <summary>
    /// Timeout in seconds for outbox entry processing.
    /// </summary>
    public const int ProcessingTimeoutSeconds = 30;

    /// <summary>
    /// Delay in milliseconds between outbox processing cycles.
    /// </summary>
    public const int ProcessingCycleDelayMs = 1000;
}
