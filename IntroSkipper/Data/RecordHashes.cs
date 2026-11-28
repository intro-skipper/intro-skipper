// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// The two hashes an analysis record is written under. <see cref="ConfigHash"/> covers every
/// setting that shapes the mode's result (<c>ConfigHasher.Analysis</c>). <see cref="KeepHash"/>
/// covers only what still reopens a result while <c>KeepExistingAnalysis</c> is on
/// (<c>ConfigHasher.Keep</c>).
/// </summary>
/// <param name="ConfigHash">The configuration hash of the mode under the season's action.</param>
/// <param name="KeepHash">The keep hash of the mode under the season's action.</param>
public readonly record struct RecordHashes(string ConfigHash, string KeepHash);
