// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A scene of black frames.
/// </summary>
/// <param name="StartFrame">The frame number of the first black frame.</param>
/// <param name="EndFrame">The frame number of the last black frame.</param>
/// <param name="StartTime">The time of the first black frame.</param>
/// <param name="EndTime">The time of the last black frame.</param>
public record CreditScene(int StartFrame, int EndFrame, double StartTime, double EndTime);
