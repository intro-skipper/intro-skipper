// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Represents keyframe data with duration and keyframe timestamps in ticks.
/// </summary>
public sealed record KeyframeData(long DurationTicks, IReadOnlyList<long> KeyframeTicks);
