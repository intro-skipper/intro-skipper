// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// The key columns of a detection cache entry, without its payload — for consumers that
/// only need the scanned range (e.g. scan-anchor recovery).
/// </summary>
/// <param name="Type">Cache entry type.</param>
/// <param name="Mode">Analysis mode the entry was written for.</param>
/// <param name="Start">Start of the analyzed range in seconds.</param>
/// <param name="End">End of the analyzed range in seconds.</param>
/// <param name="ConfigHash">Configuration hash that produced the entry, identifying its analysis era.</param>
public sealed record DetectionCacheEntryRange(CacheEntryType Type, AnalysisMode Mode, double Start, double End, string ConfigHash);
