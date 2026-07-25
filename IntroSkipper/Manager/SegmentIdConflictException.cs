// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Thrown when a caller-supplied media segment id already identifies another Jellyfin
/// segment row, so inserting it would violate the primary key.
/// </summary>
/// <param name="segmentId">The conflicting segment id.</param>
public sealed class SegmentIdConflictException(Guid segmentId)
    : InvalidOperationException($"A media segment with id '{segmentId}' already exists.")
{
    /// <summary>
    /// Gets the conflicting segment id.
    /// </summary>
    public Guid SegmentId { get; } = segmentId;
}
