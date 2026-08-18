// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Exposes projection enablement and host configuration transitions.</summary>
internal interface ISegmentProjectionConfiguration
{
    /// <summary>Raised when projection enablement changes.</summary>
    event EventHandler<bool>? EnabledChanged;

    /// <summary>Gets a value indicating whether Jellyfin projection is enabled.</summary>
    bool Enabled { get; }
}
