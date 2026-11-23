// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One frame of a cut decode, which the credits cut start reads.
/// </summary>
/// <param name="Time">The frame's time, in media seconds.</param>
/// <param name="Visual">The frame's visual. Its frame difference is to the frame decoded before it, 0 on the decode's first.</param>
public sealed record CutFrame(double Time, KeyframeVisual Visual);
