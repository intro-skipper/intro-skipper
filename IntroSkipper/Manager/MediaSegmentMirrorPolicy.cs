// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Single home of the "Jellyfin is only a mirror" policy: every write into Jellyfin's
/// MediaSegments table is controlled by the <c>UpdateMediaSegments</c> configuration
/// flag. <see cref="MediaSegmentMirror"/> consults it on every write so callers cannot
/// forget the gate; <see cref="MediaSegmentRefreshService"/> additionally checks it to
/// skip logging and per-item library resolution for syncs that would all no-op. Reads
/// are never gated.
/// </summary>
internal static class MediaSegmentMirrorPolicy
{
    /// <summary>
    /// Gets a value indicating whether plugin segments are mirrored into Jellyfin.
    /// Defaults to enabled when no plugin instance is available (unit-test hosts).
    /// </summary>
    internal static bool Enabled => Plugin.Instance?.Configuration.UpdateMediaSegments ?? true;
}
