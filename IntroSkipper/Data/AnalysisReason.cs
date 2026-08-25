// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Why a season's episodes were queued for analysis instead of being restored from stored state.
/// Logged per season and mode so a full-library rescan can be traced back to its cause.
/// </summary>
public enum AnalysisReason
{
    /// <summary>
    /// Every episode was restored from stored state. Nothing needs analyzing.
    /// </summary>
    None,

    /// <summary>
    /// The stored analysis configuration hash no longer matches the computed one. Caused by a changed
    /// setting or by a change in Chromaprint availability, which is folded into the hash for the
    /// Introduction, Credits, and Recap modes.
    /// </summary>
    ConfigHashChanged,

    /// <summary>
    /// No analysis state is stored for this season and mode. Expected for a season analyzed for the
    /// first time, and also seen when stored state was deleted, for example by a cache cleanup that
    /// ran against an incomplete library inventory.
    /// </summary>
    NoStoredState,

    /// <summary>
    /// Stored state matches, but it does not list these episodes. Either they joined the season after
    /// it was last analyzed, or something deliberately cleared the recorded list, such as a season
    /// rescan, a settled-season reset, or deleting a segment.
    /// </summary>
    NotRecorded,
}
