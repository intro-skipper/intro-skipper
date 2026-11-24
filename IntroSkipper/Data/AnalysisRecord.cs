// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One episode's stored analysis state for a mode, as queue verification reads it.
/// </summary>
/// <param name="ConfigHash">Configuration hash the episode was analyzed under.</param>
/// <param name="FileVersion">File version at analysis time; null when the record makes no claim about the file.</param>
/// <param name="KeepHash">
/// Keep hash the record was written or last confirmed under (<see cref="RecordHashes.KeepHash"/>).
/// Null for records written before the column existed, imported from the legacy database or
/// re-armed by a segment restore. Empty when a re-check request found it null.
/// </param>
public readonly record struct AnalysisRecord(string ConfigHash, long? FileVersion, string? KeepHash);
