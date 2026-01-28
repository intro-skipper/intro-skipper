// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace IntroSkipper.Data;

/// <summary>
/// Types of outbox operations.
/// </summary>
public enum OutboxOperation
{
    /// <summary>
    /// Create or update a segment.
    /// </summary>
    Upsert,

    /// <summary>
    /// Delete a segment.
    /// </summary>
    Delete
}
