// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Domain reasons why an intent was rejected before authoritative commit.</summary>
public enum SegmentChangeRejectedReason
{
    /// <summary>The item ID is empty.</summary>
    EmptyItemId,

    /// <summary>The mode or tick range is invalid.</summary>
    InvalidModeOrRange,

    /// <summary>The segment ID or tick range is invalid.</summary>
    InvalidSegmentIdOrRange,

    /// <summary>The segment ID is empty.</summary>
    EmptySegmentId,

    /// <summary>The addressed segment is absent or suppressed.</summary>
    SegmentMissingOrSuppressed,

    /// <summary>The external segment ID or type is invalid.</summary>
    InvalidExternalIdOrType,

    /// <summary>The external segment does not exist.</summary>
    ExternalSegmentNotFound,

    /// <summary>The external segment belongs to another item.</summary>
    ExternalItemMismatch,

    /// <summary>The external segment has another type.</summary>
    ExternalTypeMismatch,

    /// <summary>The resolved external target is not the exact addressed row.</summary>
    ExternalIdMismatch,

    /// <summary>The user timestamp set is invalid.</summary>
    InvalidUserTimestamps,

    /// <summary>The season ID is empty.</summary>
    EmptySeasonId,

    /// <summary>The intent type is not supported.</summary>
    UnsupportedIntent
}
