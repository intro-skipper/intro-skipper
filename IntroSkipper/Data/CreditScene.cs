// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A scene of blackframe matches.
/// </summary>
/// <param name="StartFrame">The frame number of the first blackframe match.</param>
/// <param name="EndFrame">The frame number of the last blackframe match.</param>
/// <param name="StartTime">The time of the first blackframe match.</param>
/// <param name="EndTime">The time of the last blackframe match.</param>
public record CreditScene(int StartFrame, int EndFrame, double StartTime, double EndTime);
