// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Injectable view of <see cref="MediaSegmentMirrorPolicy"/> for consumers that need
/// enablement transitions, not just the current value. <see cref="Enabled"/> reads the
/// live configuration on every call — implementations never cache a serving copy, so
/// the policy keeps a single home.
/// </summary>
internal interface IMediaSegmentMirrorPolicy
{
    /// <summary>Raised when the mirroring flag flips.</summary>
    event EventHandler<bool>? EnabledChanged;

    /// <summary>Gets a value indicating whether plugin segments are mirrored into Jellyfin.</summary>
    bool Enabled { get; }
}
