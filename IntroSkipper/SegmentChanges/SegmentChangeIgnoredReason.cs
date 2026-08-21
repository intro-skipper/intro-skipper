// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Expected reasons why a valid intent made no authoritative change.</summary>
public enum SegmentChangeIgnoredReason
{
    /// <summary>The requested user segment already exists.</summary>
    UserSegmentAlreadyExists,

    /// <summary>The requested user segment image already exists.</summary>
    UserImageAlreadyExists,

    /// <summary>The segment already has the requested values.</summary>
    SegmentAlreadyHasValues,

    /// <summary>The segment is absent or was already deleted.</summary>
    SegmentMissingOrDeleted,

    /// <summary>The segment is absent or is not suppressed.</summary>
    SegmentMissingOrNotSuppressed,

    /// <summary>The item is already visible.</summary>
    AlreadyVisible,

    /// <summary>The item is already hidden.</summary>
    AlreadyHidden,

    /// <summary>The timestamp payload contained no valid slots.</summary>
    NoValidUserTimestamps
}
