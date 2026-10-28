// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A scene of black keyframes, located by position in the keyframe scan. ffmpeg's blackframe
/// frame number cannot locate a keyframe: it restarts at 0 whenever ffmpeg rebuilds the
/// filtergraph, as it does when the video changes resolution mid-stream.
/// </summary>
/// <param name="StartIndex">The scan position of the first black keyframe.</param>
/// <param name="EndIndex">The scan position of the last black keyframe.</param>
/// <param name="StartTime">The time of the first black keyframe.</param>
/// <param name="EndTime">The time of the last black keyframe.</param>
public sealed record CreditScene(int StartIndex, int EndIndex, double StartTime, double EndTime);
