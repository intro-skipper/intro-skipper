// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

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
