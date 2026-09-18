// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-keyframe luma statistics used to detect credits on a near-uniform card.
/// </summary>
/// <remarks>
/// All from the <c>signalstats</c> filter in the same keyframe decode as the black-frame scan, on the
/// 8-bit limited-range scale the graph pins with <c>format=yuv420p</c>. A card is a dominant background
/// (<see cref="LumaLow"/> to <see cref="LumaHigh"/> within a few levels) with text far from it
/// (<see cref="LumaMin"/> or <see cref="LumaMax"/> far outside that band).
/// </remarks>
/// <param name="Time">Keyframe time relative to the credits fingerprint start.</param>
/// <param name="LumaMin">Lowest luma in the frame (<c>YMIN</c>).</param>
/// <param name="LumaLow">10th percentile luma (<c>YLOW</c>).</param>
/// <param name="LumaHigh">90th percentile luma (<c>YHIGH</c>).</param>
/// <param name="LumaMax">Highest luma in the frame (<c>YMAX</c>).</param>
/// <param name="Saturation">Mean saturation (<c>SATAVG</c>, 0..255).</param>
public sealed record KeyframeVisual(double Time, double LumaMin, double LumaLow, double LumaHigh, double LumaMax, double Saturation);
