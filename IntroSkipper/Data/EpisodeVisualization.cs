// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Episode name and internal ID as returned by the visualization controller.
/// </summary>
/// <param name="Id">Episode id.</param>
/// <param name="Name">Episode name.</param>
public record EpisodeVisualization(Guid Id, string Name);
