// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Single home of the "Jellyfin is only a mirror" policy: every write into Jellyfin's
/// MediaSegments table is controlled by the <c>UpdateMediaSegments</c> configuration
/// flag. The writing services (<see cref="MediaSegmentEditorService"/> and
/// <see cref="MediaSegmentRefreshService"/>) consult it themselves so callers cannot
/// forget the gate; reads are never gated.
/// </summary>
internal static class MediaSegmentMirrorPolicy
{
    /// <summary>
    /// Gets a value indicating whether plugin segments are mirrored into Jellyfin.
    /// Defaults to enabled when no plugin instance is available (unit-test hosts).
    /// </summary>
    internal static bool Enabled => Plugin.Instance?.Configuration.UpdateMediaSegments ?? true;
}
