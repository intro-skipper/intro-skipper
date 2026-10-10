// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// What the subtitle analyzer left for the rest of the analyzer chain to settle for one
/// episode and mode.
/// </summary>
public enum SubtitleOutcome
{
    /// <summary>
    /// Nothing to settle: subtitle detection did not run for the episode, or it wrote or
    /// retired its segment from a complete scan.
    /// </summary>
    None,

    /// <summary>
    /// The scan was incomplete, or the pattern is not a valid .NET regular expression.
    /// Standing rows stay as the fallback and the mode fails, so a later pass retries it.
    /// </summary>
    Unresolved,

    /// <summary>
    /// A complete scan matched but gave nothing to write: admission rejected the candidate (a
    /// tombstone or user segment), or a recap cue had no reliable end. Standing rows stay and
    /// the mode settles, since a retry would end the same way.
    /// </summary>
    Rejected,
}
