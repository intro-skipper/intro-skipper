// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Result of <see cref="MediaSegmentMirror.DeleteSegmentAsync"/>. Distinguishes a
/// missing row from a disabled mirror, so a config flip mid-flight cannot masquerade
/// as drift.
/// </summary>
public enum MirrorDeleteOutcome
{
    /// <summary>Mirroring is disabled; Jellyfin was not touched.</summary>
    MirroringDisabled,

    /// <summary>The row existed and was deleted.</summary>
    Deleted,

    /// <summary>No row exists under the id. For a caller that expected the row, this is
    /// the drift signal.</summary>
    RowNotFound,
}
