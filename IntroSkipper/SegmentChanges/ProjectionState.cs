// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Durable projection lifecycle of one item's recorded work.</summary>
public enum ProjectionState
{
    /// <summary>Projection work is recorded and waiting to be applied.</summary>
    Pending,

    /// <summary>No work is pending; Jellyfin reflects the last accepted change.</summary>
    Applied,

    /// <summary>Mirroring is disabled; the recorded work stays durable and replays on enable.</summary>
    Skipped
}

/// <summary>
/// Result of <see cref="ISegmentProjectionAdapter.ApplyAsync"/>. A disabled mirror is
/// an outcome, not a failure: the work stays journaled without arming backoff or
/// recording an error, and replays when mirroring turns on. Real failures throw.
/// </summary>
internal enum ProjectionApplyOutcome
{
    /// <summary>Jellyfin converged on the item's current truth.</summary>
    Applied,

    /// <summary>Mirroring is disabled; nothing was pushed and the work must stay pending.</summary>
    MirroringDisabled,
}

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
    AlreadyHidden
}

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

    /// <summary>The user timestamp set is invalid.</summary>
    InvalidUserTimestamps,

    /// <summary>The season ID is empty.</summary>
    EmptySeasonId,

    /// <summary>The intent type is not supported.</summary>
    UnsupportedIntent
}
