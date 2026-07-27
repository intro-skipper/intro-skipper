// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Result of one legacy database import.
/// </summary>
/// <param name="SegmentsImported">Number of segment rows imported.</param>
/// <param name="SegmentsSkipped">Number of legacy segment rows skipped (invalid or duplicate).</param>
/// <param name="SeasonStatesImported">Number of season-state rows imported.</param>
/// <param name="Notes">Detected legacy shape diagnostics.</param>
internal sealed record LegacyImportResult(int SegmentsImported, int SegmentsSkipped, int SeasonStatesImported, string Notes);
