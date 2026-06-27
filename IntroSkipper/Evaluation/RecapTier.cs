// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// Which precedence tier produced a recap detection. Ordered by precision then cost, per RFC D §2.1.
/// </summary>
internal enum RecapTier
{
    /// <summary>
    /// No tier produced a detection.
    /// </summary>
    None = 0,

    /// <summary>
    /// Tier 1 — chapter marker (regex/SponsorBlock). Highest precision, lowest cost.
    /// </summary>
    Chapter = 1,

    /// <summary>
    /// Tier 2 — subtitle "previously on" phrase + dense cue cluster (spike A).
    /// </summary>
    Subtitle = 2,

    /// <summary>
    /// Tier 3 — shared audio sting + black-frame structure (the hardened existing path, spike C).
    /// </summary>
    Sting = 3,
}
