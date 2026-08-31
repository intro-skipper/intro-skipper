// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Why an item remains queued for analysis instead of being restored from stored state.
/// </summary>
public enum AnalysisReason
{
    /// <summary>
    /// Stored analysis state matches the current configuration.
    /// </summary>
    None,

    /// <summary>
    /// The stored analysis hash differs from the current configuration hash.
    /// </summary>
    ConfigHashChanged,

    /// <summary>
    /// No analysis hash is stored for the item and mode.
    /// </summary>
    NoStoredState,

    /// <summary>
    /// The item was not included in the stored analysis state.
    /// </summary>
    NotRecorded,
}
