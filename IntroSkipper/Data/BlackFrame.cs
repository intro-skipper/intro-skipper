// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A frame of video that partially (or entirely) consists of black pixels.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BlackFrame"/> class.
/// </remarks>
/// <param name="Percentage">Percentage of the frame that is black.</param>
/// <param name="Time">Time this frame appears at.</param>
/// <param name="Frame">Frame number.</param>
public record BlackFrame(int Percentage, double Time, int Frame);
