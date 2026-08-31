// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Result of <see cref="MediaSegmentMirror.SyncItemAsync"/>. Distinguishes a converged
/// mirror from a disabled one, so a caller that must not report unpushed work as done
/// (the durable projection) learns the truth from the same check that gated the write
/// instead of re-reading the flag afterwards.
/// </summary>
public enum MirrorSyncOutcome
{
    /// <summary>Mirroring is disabled; Jellyfin was not touched.</summary>
    MirroringDisabled,

    /// <summary>Jellyfin's rows converged on the plugin database (a matching mirror
    /// counts: the skip-when-unchanged comparison verified it).</summary>
    Synced,
}
