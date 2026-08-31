// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Single home of the "Jellyfin is only a mirror" policy: every write into Jellyfin's
/// MediaSegments table is controlled by the <c>UpdateMediaSegments</c> configuration
/// flag. <see cref="MediaSegmentMirror"/> consults it on every write so callers cannot
/// forget the gate; the projection worker consults the same flag through
/// <see cref="IMediaSegmentMirrorPolicy"/> so journaled work sits durably while
/// mirroring is off. Reads are never gated.
/// </summary>
internal static class MediaSegmentMirrorPolicy
{
    /// <summary>
    /// Gets a value indicating whether plugin segments are mirrored into Jellyfin.
    /// Defaults to enabled when no plugin instance is available (unit-test hosts).
    /// </summary>
    internal static bool Enabled => Plugin.Instance?.Configuration.UpdateMediaSegments ?? true;
}
