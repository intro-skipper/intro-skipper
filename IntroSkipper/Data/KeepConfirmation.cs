// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Records queue verification settled whose stored keep hash differs from the one to store, for
/// <c>IntroSkipperDatabase.ConfirmKeepHashesAsync</c>.
/// </summary>
/// <param name="Mode">Analysis mode of the records.</param>
/// <param name="Hashes">The configuration hash the records were judged under, and the keep hash to store.</param>
/// <param name="ItemIds">The items whose records to confirm.</param>
public readonly record struct KeepConfirmation(AnalysisMode Mode, RecordHashes Hashes, IReadOnlyCollection<Guid> ItemIds);
