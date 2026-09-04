// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-keyframe visual statistics used to detect non-black credits that the
/// black-frame scan is blind to (text on a near-uniform low-saturation card).
/// </summary>
/// <remarks>
/// Both signals are emitted by stock FFmpeg in the same keyframe decode as the
/// black-frame scan (<c>entropy</c> and <c>signalstats</c> filters), so acquiring
/// them costs only the additional metadata parse.
/// </remarks>
/// <param name="Time">Keyframe time relative to the credits fingerprint start.</param>
/// <param name="Entropy">Normalised luma histogram entropy (0..1); low values mark a near-uniform "card" background.</param>
/// <param name="Saturation">Mean saturation (<c>SATAVG</c>, 0..255); low values mark greyscale/muted backgrounds.</param>
public sealed record KeyframeVisual(double Time, double Entropy, double Saturation);
