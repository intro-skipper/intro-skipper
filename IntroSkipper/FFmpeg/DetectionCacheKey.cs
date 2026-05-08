// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Identifies a detection-cache entry.
/// </summary>
/// <param name="ItemId">Media item identifier.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="Type">Cache entry type.</param>
/// <param name="Start">Range start in seconds.</param>
/// <param name="End">Range end in seconds.</param>
public readonly record struct DetectionCacheKey(Guid ItemId, AnalysisMode Mode, CacheEntryType Type, double Start, double End);
