// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A time range in absolute seconds.
/// </summary>
/// <param name="Start">Range start in seconds.</param>
/// <param name="End">Range end in seconds.</param>
public sealed record SnapRange(double Start, double End);
