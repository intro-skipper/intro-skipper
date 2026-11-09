// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One keyframe of the keyframe scan: its black-frame evidence and keyframe visuals, measured from
/// the same frame.
/// </summary>
/// <remarks>
/// The page's time is that frame's time, kept in <see cref="BlackFrame.Time"/> and never rounded,
/// because the interval probe and the cut start key their cache rows from it. A page has no visual
/// when it lies past the credits window. <see cref="BlackFrame.Frame"/> means nothing on a page.
/// </remarks>
/// <param name="Frame">The keyframe's time and black percentage.</param>
/// <param name="Visual">The keyframe's visual, or <see langword="null"/> past the credits window.</param>
public sealed record KeyframePage(BlackFrame Frame, KeyframeVisual? Visual);
