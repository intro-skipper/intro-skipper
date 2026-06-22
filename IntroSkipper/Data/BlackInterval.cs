// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A continuous interval detected by FFmpeg's blackdetect filter.
/// </summary>
/// <param name="Start">Interval start time relative to the credits fingerprint start.</param>
/// <param name="End">Interval end time relative to the credits fingerprint start.</param>
public record BlackInterval(double Start, double End);
