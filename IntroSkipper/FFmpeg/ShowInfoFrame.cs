// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// One frame as the showinfo filter logged it.
/// </summary>
/// <param name="Time">The frame's presentation time in seconds, relative to the decode start.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
internal sealed record ShowInfoFrame(double Time, int Width, int Height);
