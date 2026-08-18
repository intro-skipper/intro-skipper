// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Durable projection lifecycle.</summary>
public enum ProjectionState
{
    /// <summary>The immutable plan is waiting to be applied.</summary>
    Pending,

    /// <summary>The immutable plan was applied and compacted.</summary>
    Applied,

    /// <summary>Projection was disabled, so the image was durably skipped.</summary>
    Skipped
}
