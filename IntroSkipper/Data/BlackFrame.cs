// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 TwistedUmbrellaX
// SPDX-FileCopyrightText: 2024-2025 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A frame of video that partially (or entirely) consists of black pixels.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BlackFrame"/> class.
/// </remarks>
/// <param name="Percentage">Percentage of the frame that is black.</param>
/// <param name="Time">Time this frame appears at.</param>
/// <param name="Frame">Frame number.</param>
public record BlackFrame(int Percentage, double Time, int Frame);
