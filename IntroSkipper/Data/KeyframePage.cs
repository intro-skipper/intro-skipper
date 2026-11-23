// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One keyframe of the keyframe scan: its black percentage and keyframe visuals, measured from the
/// same frame.
/// </summary>
/// <remarks>
/// The time is never rounded, because the interval probe and the cut start key their cache rows
/// from it. A page has no visual when it lies past the credits window.
/// </remarks>
/// <param name="Time">The keyframe's time, relative to the credits fingerprint start.</param>
/// <param name="BlackPercentage">The share of the keyframe's pixels under the black-frame threshold.</param>
/// <param name="Visual">The keyframe's visual, or <see langword="null"/> past the credits window.</param>
public sealed record KeyframePage(double Time, int BlackPercentage, KeyframeVisual? Visual);
