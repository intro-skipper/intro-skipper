// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides live, read-through access to media detection configuration.
/// Each property is evaluated at call time so callers always see current settings.
/// </summary>
public interface IMediaDetectionOptions
{
    /// <summary>
    /// Gets the maximum noise level (in dB) for silence detection.
    /// </summary>
    int SilenceDetectionMaximumNoise { get; }
}
