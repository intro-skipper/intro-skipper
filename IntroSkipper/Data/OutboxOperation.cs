// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Represents the type of operation to perform on a segment.
/// </summary>
public enum OutboxOperation
{
    /// <summary>
    /// Insert or update a segment.
    /// </summary>
    Upsert,

    /// <summary>
    /// Delete a segment.
    /// </summary>
    Delete
}
