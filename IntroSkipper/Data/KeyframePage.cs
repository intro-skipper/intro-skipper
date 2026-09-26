// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One keyframe of the keyframe scan: its black-frame row and its visual, when it has one.
/// </summary>
/// <remarks>
/// The page's time is its black-frame time, never rounded, because the lead-in, interval and
/// boundary probes key their cache rows from it. A page has no visual when it lies past the credits
/// window, when no visual lies within 10 ms of its row, or when the ffmpeg build has no signalstats
/// filter.
/// </remarks>
/// <param name="Frame">The keyframe's black-frame row.</param>
/// <param name="Visual">The keyframe's visual, or <see langword="null"/> when the scan has none for it.</param>
public sealed record KeyframePage(BlackFrame Frame, KeyframeVisual? Visual);
