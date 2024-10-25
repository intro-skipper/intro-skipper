// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

namespace IntroSkipper.Data;

/// <summary>
/// Taken from https://kodi.wiki/view/Edit_decision_list#MPlayer_EDL.
/// </summary>
public enum EdlAction
{
    /// <summary>
    /// Do not create EDL files.
    /// </summary>
    None = -1,

    /// <summary>
    /// Completely remove the segment from playback as if it was never in the original video.
    /// </summary>
    Cut = 0,

    /// <summary>
    /// Mute audio, continue playback.
    /// </summary>
    Mute = 1,

    /// <summary>
    /// Inserts a new scene marker.
    /// </summary>
    SceneMarker = 2,

    /// <summary>
    /// Automatically skip once during playback.
    /// </summary>
    CommercialBreak = 3
}
