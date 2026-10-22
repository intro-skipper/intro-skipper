// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-keyframe luma statistics used to detect credits on a near-uniform card, and the frame
/// difference that places a credits part's start on the cut before its first keyframe.
/// </summary>
/// <remarks>
/// All from the <c>signalstats</c> filter in the same keyframe decode as the black-frame scan, on the
/// 8-bit scale of <c>format=yuv420p</c>. That conversion keeps the source's range, so a full-range
/// source spans 0 to 255. A cut decode reads every frame of one keyframe gap with the same filters
/// at the same resolution, so its frame differences compare directly with a page's. A card is a dominant background
/// (<see cref="LumaLow"/> to <see cref="LumaHigh"/> within a few levels) with text far from it
/// (<see cref="LumaMin"/> or <see cref="LumaMax"/> far outside that band).
/// </remarks>
/// <param name="Time">Keyframe time relative to the credits fingerprint start; for a frame of a cut decode, media time.</param>
/// <param name="LumaMin">Lowest luma in the frame (<c>YMIN</c>).</param>
/// <param name="LumaLow">10th percentile luma (<c>YLOW</c>).</param>
/// <param name="LumaHigh">90th percentile luma (<c>YHIGH</c>).</param>
/// <param name="LumaMax">Highest luma in the frame (<c>YMAX</c>).</param>
/// <param name="Saturation">Mean saturation (<c>SATAVG</c>, 0..255).</param>
/// <param name="FrameDifference">Mean absolute luma difference to the frame decoded before it (<c>YDIF</c>), 0 on the first frame of a decode. In the keyframe scan the frame before is the previous keyframe, so a large difference says the picture changed somewhere between the two.</param>
public sealed record KeyframeVisual(double Time, double LumaMin, double LumaLow, double LumaHigh, double LumaMax, double Saturation, double FrameDifference);
